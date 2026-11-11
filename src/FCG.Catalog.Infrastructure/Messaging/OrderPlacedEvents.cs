using FCG.Catalog.Application.Orders;
using FCG.Contracts;
using MassTransit;

namespace FCG.Catalog.Infrastructure.Messaging;

public sealed class OrderPlacedEvents(IPublishEndpoint publish) : IOrderPlacedEvents
{
    public Task StageAsync(OrderPlacedEvent message, CancellationToken cancellationToken) =>
        publish.Publish(message, context => { context.MessageId = message.OrderId; context.CorrelationId = message.OrderId; }, cancellationToken);
}
