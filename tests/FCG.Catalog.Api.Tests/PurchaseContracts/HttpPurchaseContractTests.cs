using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
namespace PurchaseContracts;
public sealed class HttpPurchaseContractTests
{
    private static Type Contract(string name) { var type=Assembly.Load("FCG.Catalog.Application").GetType("FCG.Contracts."+name); Assert.NotNull(type); return type; }
    private static object Read(string name,string json) => JsonSerializer.Deserialize(json, Contract(name))!;
    private const string Id="11111111-1111-1111-1111-111111111111";
    private const string Game="33333333-3333-3333-3333-333333333333";
    [Fact]
    public void Request_rejects_empty_game_and_user_selected_identity()
    {
        Assert.NotNull(Read("PurchaseRequest", "{\"gameId\":\""+Game+"\"}"));
        var type=Contract("PurchaseRequest");
        Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize("{}",type));
        Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize("{\"gameId\":\""+Game+"\",\"userId\":\""+Id+"\"}",type));
    }
    [Fact]
    public void Acceptance_is_Pending_with_own_order_location()
    {
        var json="{\"orderId\":\""+Id+"\",\"gameId\":\""+Game+"\",\"price\":44.93,\"status\":\"Pending\"}";
        var value=Read("PurchaseAcceptedResponse",json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json),JsonNode.Parse(JsonSerializer.Serialize(value))));
        Assert.Equal("/api/v1/me/orders/"+Id,value.GetType().GetProperty("Location")!.GetValue(value));
    }
    [Theory]
    [InlineData("Pending",null,null)]
    [InlineData("Approved","2026-10-19T12:34:58.123456Z",null)]
    [InlineData("Rejected","2026-10-19T12:34:58.123456Z","AmountAboveLimit")]
    [InlineData("Approved","2026-10-19T12:34:55.123456Z",null)]
    public void Detail_has_exactly_seven_fields_and_explicit_nulls(string status,string? completed,string? reason)
    {
        var json=Detail(status,completed,reason);
        var value=Read("PurchaseOrderResponse",json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json),JsonNode.Parse(JsonSerializer.Serialize(value))));
    }
    [Theory]
    [InlineData("Pending","2026-10-19T12:34:58.123456Z",null)]
    [InlineData("Approved",null,null)]
    [InlineData("Approved","2026-10-19T12:34:58.123456Z","AmountAboveLimit")]
    [InlineData("Rejected","2026-10-19T12:34:58.123456Z",null)]
    [InlineData("Rejected","2026-10-19T12:34:58.123456Z","Other")]
    [InlineData("Unknown",null,null)]
    public void Detail_rejects_inconsistent_lifecycle(string status,string? completed,string? reason)
    { var type=Contract("PurchaseOrderResponse"); Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize(Detail(status,completed,reason),type)); }
    [Fact]
    public void Pagination_has_inherited_defaults_bounds_and_envelope()
    {
        var type=Contract("PurchaseOrdersQuery");
        var value=Activator.CreateInstance(type)!;
        Assert.Equal(1,type.GetProperty("Page")!.GetValue(value)); Assert.Equal(20,type.GetProperty("PageSize")!.GetValue(value));
        foreach(var args in new[]{new object[]{0,20},new object[]{1000001,20},new object[]{1,0},new object[]{1,101}})
            Assert.Throws<TargetInvocationException>(()=>Activator.CreateInstance(type,args));
        Assert.NotNull(Activator.CreateInstance(type,[1000000,100]));
        var envelope="{\"items\":[],\"page\":1,\"pageSize\":20,\"totalCount\":0}";
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(envelope),JsonNode.Parse(JsonSerializer.Serialize(Read("PurchaseOrdersPage",envelope)))));
    }
    [Fact]
    public void Pending_conflict_preserves_problem_details_and_existing_order_reference()
    {
        var type=Contract("PendingOrderConflict");
        var value=Activator.CreateInstance(type,[Guid.Parse(Id),"/api/v1/me/library","trace-purchase-01"])!;
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(value));var root=json.RootElement;
        Assert.Equal(409,root.GetProperty("status").GetInt32());
        Assert.Equal("order_already_pending",root.GetProperty("code").GetString());
        Assert.Equal("urn:fcg:error:order-already-pending",root.GetProperty("type").GetString());
        Assert.Equal(Id,root.GetProperty("orderId").GetString());
        Assert.Equal("/api/v1/me/orders/"+Id,root.GetProperty("location").GetString());
        Assert.Equal("trace-purchase-01",root.GetProperty("traceId").GetString());
        Assert.Equal("/api/v1/me/library",root.GetProperty("instance").GetString());
    }
    [Fact]
    public void Acceptance_cannot_silently_change_a_non_pending_status()
    {
        var type=Contract("PurchaseAcceptedResponse");
        Assert.ThrowsAny<Exception>(()=>JsonSerializer.Deserialize("{\"orderId\":\""+Id+"\",\"gameId\":\""+Game+"\",\"price\":44.93,\"status\":\"Approved\"}",type));
    }
    private static string Detail(string status,string? completed,string? reason)=>JsonSerializer.Serialize(new {orderId=Id,gameId=Game,price=44.93m,status,placedAtUtc="2026-10-19T12:34:56.123456Z",completedAtUtc=completed,rejectionReason=reason});
}
