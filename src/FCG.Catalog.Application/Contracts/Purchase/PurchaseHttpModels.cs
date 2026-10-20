using System.Text.Json.Serialization;

namespace FCG.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequest
{
    [JsonPropertyName("gameId")] public Guid GameId { get; }
    [JsonConstructor] public PurchaseRequest(Guid gameId) => GameId = PurchaseValues.Id(gameId, nameof(gameId));
}

public sealed record PurchaseAcceptedResponse
{
    [JsonPropertyName("orderId")] public Guid OrderId { get; }
    [JsonPropertyName("gameId")] public Guid GameId { get; }
    [JsonPropertyName("price"), JsonConverter(typeof(PurchasePriceConverter))] public decimal Price { get; }
    [JsonPropertyName("status")] public string Status { get; }
    [JsonIgnore] public string Location => $"/api/v1/me/orders/{OrderId:D}";

    public PurchaseAcceptedResponse(Guid orderId, Guid gameId, decimal price) : this(orderId, gameId, price, "Pending") { }

    [JsonConstructor]
    public PurchaseAcceptedResponse(Guid orderId, Guid gameId, decimal price, string status)
    {
        if (status != "Pending") throw new ArgumentException("An accepted purchase must be Pending.", nameof(status));
        Status = status;
        OrderId = PurchaseValues.Id(orderId, nameof(orderId));
        GameId = PurchaseValues.Id(gameId, nameof(gameId));
        Price = PurchaseValues.Price(price);
    }
}

public sealed record PurchaseOrderResponse
{
    [JsonPropertyName("orderId")] public Guid OrderId { get; }
    [JsonPropertyName("gameId")] public Guid GameId { get; }
    [JsonPropertyName("price"), JsonConverter(typeof(PurchasePriceConverter))] public decimal Price { get; }
    [JsonPropertyName("status")] public string Status { get; }
    [JsonPropertyName("placedAtUtc"), JsonConverter(typeof(PurchaseInstantConverter))] public DateTimeOffset PlacedAtUtc { get; }
    [JsonPropertyName("completedAtUtc"), JsonIgnore(Condition = JsonIgnoreCondition.Never), JsonConverter(typeof(PurchaseInstantConverter))]
    public DateTimeOffset? CompletedAtUtc { get; }
    [JsonPropertyName("rejectionReason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? RejectionReason { get; }

    [JsonConstructor]
    public PurchaseOrderResponse(Guid orderId, Guid gameId, decimal price, string status,
        DateTimeOffset placedAtUtc, DateTimeOffset? completedAtUtc, string? rejectionReason)
    {
        OrderId = PurchaseValues.Id(orderId, nameof(orderId));
        GameId = PurchaseValues.Id(gameId, nameof(gameId));
        Price = PurchaseValues.Price(price);
        PlacedAtUtc = PurchaseValues.Instant(placedAtUtc);
        if (status == "Pending")
        {
            if (completedAtUtc is not null || rejectionReason is not null)
                throw new ArgumentException("Pending cannot have a completion or rejection reason.");
        }
        else
        {
            PurchaseValues.Result(status, rejectionReason);
            if (completedAtUtc is null)
                throw new ArgumentException("A terminal order requires its recorded completion instant.");
        }
        if (completedAtUtc is not null) PurchaseValues.Instant(completedAtUtc.Value);
        Status = status;
        CompletedAtUtc = completedAtUtc;
        RejectionReason = rejectionReason;
    }
}

public sealed record PurchaseOrdersQuery
{
    public int Page { get; }
    public int PageSize { get; }
    public PurchaseOrdersQuery() : this(1, 20) { }
    public PurchaseOrdersQuery(int page, int pageSize)
    {
        if (page is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        Page = page;
        PageSize = pageSize;
    }
}

public sealed record PurchaseOrdersPage
{
    [JsonPropertyName("items")] public IReadOnlyList<PurchaseOrderResponse> Items { get; }
    [JsonPropertyName("page")] public int Page { get; }
    [JsonPropertyName("pageSize")] public int PageSize { get; }
    [JsonPropertyName("totalCount")] public int TotalCount { get; }
    [JsonConstructor]
    public PurchaseOrdersPage(IReadOnlyList<PurchaseOrderResponse> items, int page, int pageSize, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        var query = new PurchaseOrdersQuery(page, pageSize);
        if (totalCount < 0 || items.Count > pageSize || items.Count > totalCount)
            throw new ArgumentOutOfRangeException(nameof(totalCount));
        Items = Array.AsReadOnly(items.ToArray());
        Page = query.Page;
        PageSize = query.PageSize;
        TotalCount = totalCount;
    }
}
