using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FCG.Catalog.Api.Contracts;
using FCG.Catalog.Api.Errors;
using FCG.Catalog.Application.Orders;
using FCG.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Controllers;

[ApiController]
[Route("api/v1/me/orders")]
[Authorize(Policy = IdentityAuthorizationPolicy.UserOrAdmin)]
public sealed class OrdersController(OwnOrdersHandler handler) : ControllerBase
{
    /// <summary>Lists only the caller's orders, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<PurchaseOrdersPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] ListOrdersRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            return Unauthorized(ApiErrors.Unauthenticated.ToProblemDetails(Request.Path));
        return Ok(await handler.ListAsync(userId, request.Page, request.PageSize, cancellationToken));
    }

    /// <summary>Returns an order owned by the caller; another owner's ID is indistinguishable from a missing ID.</summary>
    /// <remarks>The first read after acceptance can already observe Approved or Rejected.</remarks>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PurchaseOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            return Unauthorized(ApiErrors.Unauthenticated.ToProblemDetails(Request.Path));
        var result = await handler.FindAsync(userId, id, cancellationToken);
        return result is null ? NotFound(ApiErrors.ResourceNotFound.ToProblemDetails(Request.Path)) : Ok(result);
    }
}
