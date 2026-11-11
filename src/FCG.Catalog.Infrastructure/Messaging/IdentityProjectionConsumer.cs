using FCG.Catalog.Infrastructure.Identity;
using FCG.Contracts;
using MassTransit;

namespace FCG.Catalog.Infrastructure.Messaging;

public sealed class IdentityProjectionConsumer(IdentityProjection projection)
    : IConsumer<UserCreatedEvent>, IConsumer<UserStateChangedEvent>, IConsumer<UserStateSnapshotEvent>
{
    public Task Consume(ConsumeContext<UserCreatedEvent> context) => projection.HandleAsync(context.Message, context.MessageId, context.CancellationToken);
    public Task Consume(ConsumeContext<UserStateChangedEvent> context) => projection.HandleAsync(context.Message, context.MessageId, context.CancellationToken);
    public Task Consume(ConsumeContext<UserStateSnapshotEvent> context) => projection.HandleAsync(context.Message, context.MessageId, context.CancellationToken);
}
