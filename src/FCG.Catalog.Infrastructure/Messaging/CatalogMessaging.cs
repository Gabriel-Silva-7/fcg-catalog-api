using FCG.Catalog.Application.Orders;
using FCG.Catalog.Infrastructure.Persistence;
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
        services.AddSingleton(settings);
        services.AddScoped<IOrderPlacedEvents, OrderPlacedEvents>();
        services.AddMassTransit(bus =>
        {
            bus.ConfigureHealthCheckOptions(options => { options.Tags.Clear(); options.Tags.Add("diagnostic"); });
            bus.AddEntityFrameworkOutbox<CatalogDbContext>(outbox =>
            {
                outbox.UsePostgres(); outbox.DisableInboxCleanupService();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
                outbox.UseBusOutbox(options => options.MessageDeliveryTimeout = TimeSpan.FromSeconds(5));
            });
            bus.UsingRabbitMq((_, rabbit) =>
            {
                rabbit.Host(settings.Host, settings.Port, settings.VirtualHost, host =>
                { host.Username(settings.Username); host.Password(settings.Password); host.PublisherConfirmation = true; });
                rabbit.ConfigureJsonSerializerOptions(options =>
                { options.Converters.Insert(0, new PurchaseEventJsonConverter<OrderPlacedEvent>()); return options; });
            });
        });
        services.Configure<MassTransitHostOptions>(options => { options.WaitUntilStarted = false; options.StopTimeout = TimeSpan.FromSeconds(5); });
        services.AddHealthChecks().AddCheck<CatalogTransportHealthCheck>("catalog-transport", tags: ["ready"]);
        return services;
    }
}
