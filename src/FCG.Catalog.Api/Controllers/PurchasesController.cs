using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FCG.Catalog.Api.Contracts;
using FCG.Catalog.Api.Diagnostics;
using FCG.Catalog.Api.Errors;
using FCG.Catalog.Application.Orders;
using FCG.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Controllers;

[ApiController]
[Route("api/v1/me/library")]
[Authorize(Policy = IdentityAuthorizationPolicy.UserOrAdmin)]
public sealed class PurchasesController(PlaceOrderHandler handler, ILogger<PurchasesController> logger) : ControllerBase
{
    /// <summary>Accepts a purchase using the current server-selected price.</summary>
    /// <remarks>202 records a Pending order and durable event; it does not grant the game. After a lost response, consult your orders before retrying.</remarks>
    [HttpPost]
    [ProducesResponseType<PurchaseAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Purchase(PurchaseBody request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            return Unauthorized(ApiErrors.Unauthenticated.ToProblemDetails(Request.Path));
        var result = await handler.HandleAsync(userId, request.GameId!.Value, cancellationToken);
        switch (result.Status)
        {
            case PlacementStatus.GameNotFound: return NotFound(ApiErrors.ResourceNotFound.ToProblemDetails(Request.Path));
            case PlacementStatus.AlreadyAcquired: return Conflict(ApiErrors.GameAlreadyAcquired.ToProblemDetails(Request.Path));
            case PlacementStatus.AlreadyPending:
                var problem = new PendingOrderConflict(result.ExistingOrderId!.Value, Request.Path, TraceIdentity.Resolve(HttpContext));
                Response.Headers.Location = problem.Location;
                return new ObjectResult(problem) { StatusCode = 409, ContentTypes = { "application/problem+json" } };
            case PlacementStatus.Busy:
                Response.Headers.RetryAfter = "1";
                return StatusCode(503, ApiErrors.OrderPlacementUnavailable.ToProblemDetails(Request.Path));
            default:
                var accepted = result.Accepted!;
                logger.LogInformation("OrderPlaced {UserId} {GameId} {OrderId} {TraceId}", userId, accepted.GameId, accepted.OrderId, TraceIdentity.Resolve(HttpContext));
                return Accepted(accepted.Location, accepted);
        }
    }
}
