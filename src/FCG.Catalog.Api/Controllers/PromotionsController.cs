using System.IdentityModel.Tokens.Jwt;
using FCG.Catalog.Api.Contracts;
using System.Security.Claims;
using FCG.Catalog.Api.Diagnostics;
using FCG.Catalog.Api.Errors;
using FCG.Catalog.Application.Catalog;
using FCG.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Controllers;

[ApiController]
[Authorize(Policy = IdentityAuthorizationPolicy.AdminOnly)]
[Route("api/v1/games/{gameId:guid}/promotions")]
public sealed class PromotionsController(
    CreatePromotionHandler createPromotionHandler,
    ILogger<PromotionsController> logger) : ControllerBase
{
    /// <summary>
    /// Creates a promotion for an active game as an administrator.
    /// </summary>
    /// <remarks>
    /// Overlapping promotions are allowed; the catalog applies the highest active discount.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<PromotionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(
        Guid gameId,
        CreatePromotionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var actorUserId))
        {
            return Unauthorized(
                ApiErrors.Unauthenticated.ToProblemDetails(HttpContext.Request.Path));
        }

        var promotion = await createPromotionHandler.HandleAsync(
            new CreatePromotionCommand(
                actorUserId,
                gameId,
                request.DiscountPercentage!.Value,
                request.StartsAt!.Value,
                request.EndsAt!.Value),
            cancellationToken);

        if (promotion is null)
        {
            return NotFound(
                ApiErrors.ResourceNotFound.ToProblemDetails(HttpContext.Request.Path));
        }

        logger.LogInformation(
            "PromotionCreated {ActorUserId} {TargetPromotionId} {TargetGameId} {TraceId}",
            actorUserId,
            promotion.Id,
            promotion.GameId,
            TraceIdentity.Resolve(HttpContext));

        return CreatedAtRoute(
            GamesController.GetByIdRouteName,
            new { id = promotion.GameId },
            new PromotionResponse(
                promotion.Id,
                promotion.GameId,
                promotion.DiscountPercentage,
                promotion.StartsAtUtc,
                promotion.EndsAtUtc,
                promotion.IsCurrentlyActive));
    }
}
