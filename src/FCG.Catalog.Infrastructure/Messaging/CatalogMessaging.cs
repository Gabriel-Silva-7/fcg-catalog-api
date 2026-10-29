using FCG.Catalog.Application.Identity;
using FCG.Catalog.Infrastructure.Identity;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Catalog.Infrastructure.Orders;
using FCG.Contracts;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Catalog.Infrastructure.Messaging;

public static class CatalogMessaging
{
    public static IServiceCollection AddCatalogMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new(); settings.Validate();
        var queue = configuration["Queues:Payments"] ?? "fcg-catalog-payments";
        if (string.IsNullOrWhiteSpace(queue) || queue.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidOperationException("Queues:Payments requires a valid durable endpoint name.");
        var identityQueue = configuration["Queues:Identity"] ?? "fcg-catalog-identity";
        if (string.IsNullOrWhiteSpace(identityQueue) || identityQueue == queue || identityQueue.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidOperationException("Queues:Identity requires a valid distinct durable endpoint name.");
        services.AddScoped<IAuthorizationStateReader, AuthorizationStateReader>();
        services.AddScoped<IdentityProjection>();
        services.AddSingleton(settings);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<PaymentApplication>();
        services.AddScoped<IOrderPlacedEvents, OrderPlacedEvents>();
        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<PaymentProcessedConsumer>();
            bus.AddConsumer<IdentityProjectionConsumer>();
            bus.ConfigureHealthCheckOptions(options => { options.Tags.Clear(); options.Tags.Add("diagnostic"); });
            bus.AddEntityFrameworkOutbox<CatalogDbContext>(outbox =>
            {
                outbox.UsePostgres(); outbox.DisableInboxCleanupService();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
                outbox.UseBusOutbox(options => options.MessageDeliveryTimeout = TimeSpan.FromSeconds(5));
            });
            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(settings.Host, settings.Port, settings.VirtualHost, host =>
                { host.Username(settings.Username); host.Password(settings.Password); host.PublisherConfirmation = true; });
                rabbit.ConfigureJsonSerializerOptions(options =>
                {
                    options.Converters.Insert(0, new PurchaseEventJsonConverter<OrderPlacedEvent>());
                    options.Converters.Insert(0, new PurchaseEventJsonConverter<PaymentProcessedEvent>());
                    options.Converters.Insert(0, new IdentityEventJsonConverter<UserCreatedEvent>());
                    options.Converters.Insert(0, new IdentityEventJsonConverter<UserStateChangedEvent>());
                    options.Converters.Insert(0, new IdentityEventJsonConverter<UserStateSnapshotEvent>());
                    return options;
                });
                rabbit.ReceiveEndpoint(identityQueue, endpoint =>
                {
                    endpoint.UseMessageRetry(retry =>
                    {
                        retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                        retry.Ignore<ArgumentException>();
                    });
                    endpoint.ConfigureConsumer<IdentityProjectionConsumer>(context);
                });
                rabbit.ReceiveEndpoint(queue, endpoint =>
                {
                    endpoint.UseMessageRetry(retry =>
                    {
                        retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                        retry.Ignore<ArgumentException>();
                    });
                    // No outgoing effects: own durable transaction + permanent Order/library deduplication precede ack.
                    endpoint.ConfigureConsumer<PaymentProcessedConsumer>(context);
                });
            });
        });
        services.Configure<MassTransitHostOptions>(options => { options.WaitUntilStarted = false; options.StopTimeout = TimeSpan.FromSeconds(5); });
        services.AddHealthChecks().AddCheck<CatalogTransportHealthCheck>("catalog-transport", tags: ["ready"]);
        return services;
    }
}
