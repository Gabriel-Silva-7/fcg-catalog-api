using System.Text;
using System.Text.Json;
using FCG.Catalog.Api.Tests.Catalog;
using FCG.Catalog.Domain.Library;
using FCG.Catalog.Infrastructure.Persistence;
using MassTransit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;
using static FCG.Catalog.Api.Tests.Orders.PaymentApplicationTests;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class PaymentTransportFailureTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Theory]
    [InlineData("status")]
    [InlineData("price")]
    public async Task Invalid_wire_payment_goes_to_error_without_terminal_or_quarantine(string field)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app);
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync();
        var errorQueue = app.PaymentsQueue + "_error"; await channel.QueueDeclareAsync(errorQueue, durable: true, exclusive: false, autoDelete: false);
        var payload = JsonSerializer.Serialize(Payment(order));
        payload = field == "status" ? payload.Replace("\"Approved\"", "\"Unknown\"") : payload.Replace("\"price\":49.90", "\"price\":49.901");
        var envelope = JsonSerializer.Serialize(new { messageId = Guid.NewGuid(), messageType = new[] { "urn:message:FCG.Contracts:PaymentProcessedEvent" },
            message = JsonSerializer.Deserialize<JsonElement>(payload), headers = new { } });
        await channel.BasicPublishAsync("FCG.Contracts:PaymentProcessedEvent", "", mandatory: true,
            basicProperties: new BasicProperties { ContentType = "application/vnd.masstransit+json" }, body: Encoding.UTF8.GetBytes(envelope));
        BasicGetResult? failed = null; await Eventually(async () => (failed = await channel.BasicGetAsync(errorQueue, autoAck: false)) is not null);
        await channel.BasicAckAsync(failed!.DeliveryTag, multiple: false);
        Assert.Equal("Pending", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Exhausted_storage_failure_preserves_pending_and_redrive_same_message_recovers()
    {
        var failure = new FailLibraryWrites();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(failure))));
        var order = await Setup(app);
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync();
        var errorQueue = app.PaymentsQueue + "_error"; await channel.QueueDeclareAsync(errorQueue, durable: true, exclusive: false, autoDelete: false);
        await app.Services.GetRequiredService<IBus>().Publish(Payment(order));
        BasicGetResult? failed = null; await Eventually(async () => (failed = await channel.BasicGetAsync(errorQueue, autoAck: false)) is not null);
        Assert.Equal(4, failure.Attempts); Assert.Equal("Pending", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        failure.Enabled = false;
        await channel.BasicPublishAsync("", app.PaymentsQueue, mandatory: true, basicProperties: new BasicProperties(failed!.BasicProperties), body: failed.Body);
        await channel.BasicAckAsync(failed.DeliveryTag, multiple: false);
        await Eventually(async () => await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\"") == 1L);
        Assert.Equal("Approved", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.DoesNotContain("private-storage-failure", string.Join('\n', app.Logs.Entries));
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    private async Task<IConnection> Connect()
    {
        var settings = postgres.BrokerSettings;
        return await new ConnectionFactory { HostName = settings["RabbitMq:Host"], Port = int.Parse(settings["RabbitMq:Port"]), VirtualHost = "/fcg",
            UserName = settings["RabbitMq:Username"], Password = settings["RabbitMq:Password"] }.CreateConnectionAsync();
    }
    private sealed class FailLibraryWrites : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true; public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<LibraryEntry>().Any(x => x.State == EntityState.Added))
            { Attempts++; throw new InvalidOperationException("private-storage-failure"); }
            return ValueTask.FromResult(result);
        }
    }
}
