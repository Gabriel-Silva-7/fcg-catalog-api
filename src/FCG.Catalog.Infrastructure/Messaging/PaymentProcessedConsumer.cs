using FCG.Catalog.Infrastructure.Orders;
using FCG.Contracts;
using MassTransit;

namespace FCG.Catalog.Infrastructure.Messaging;

public sealed class PaymentProcessedConsumer(PaymentApplication application) : IConsumer<PaymentProcessedEvent>
{
    public async Task Consume(ConsumeContext<PaymentProcessedEvent> context) =>
        await application.HandleAsync(context.Message, context.MessageId, context.CancellationToken);
}
