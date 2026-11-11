using System.Text.Json.Serialization;

namespace FCG.Contracts;

/// <summary>Problem Details body for an existing order owned by the authenticated caller.</summary>
public sealed record PendingOrderConflict
{
    [JsonPropertyName("type")] public string Type => "urn:fcg:error:order-already-pending";
    [JsonPropertyName("title")] public string Title => "Order already pending";
    [JsonPropertyName("status")] public int Status => 409;
    [JsonPropertyName("instance")] public string Instance { get; }
    [JsonPropertyName("code")] public string Code => "order_already_pending";
    [JsonPropertyName("traceId")] public string TraceId { get; }
    [JsonPropertyName("orderId")] public Guid OrderId { get; }
    [JsonPropertyName("location")] public string Location => $"/api/v1/me/orders/{OrderId:D}";
    public PendingOrderConflict(Guid orderId, string instance, string traceId)
    {
        OrderId = PurchaseValues.Id(orderId, nameof(orderId));
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        Instance = instance;
        TraceId = traceId;
    }
}
