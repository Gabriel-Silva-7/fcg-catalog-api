using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FCG.Catalog.Api.Contracts;
using FCG.Catalog.Api.Diagnostics;
using FCG.Catalog.Api.Errors;
using FCG.Catalog.Application.Catalog;
using FCG.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Controllers;

[ApiController]
[Route("api/v1/games")]
public sealed class GamesController(
    CreateGameHandler createGameHandler,
    ListGamesHandler listGamesHandler,
    GetGameHandler getGameHandler,
    ILogger<GamesController> logger) : ControllerBase
{
    public const string GetByIdRouteName = "GetGameById";

    /// <summary>
    /// Lists the active game catalog.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResponse<GameResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] ListGamesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await listGamesHandler.HandleAsync(
            new ListGamesQuery(
                request.Search,
                request.Page,
                request.PageSize,
                request.SortBy),
            cancellationToken);

        return Ok(
            new PagedResponse<GameResponse>(
                result.Items.Select(ToResponse).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
    }

    /// <summary>
    /// Returns an active game from the public catalog.
    /// </summary>
    /// <remarks>
    /// Inactive games are removed from the public catalog and cannot receive new promotions or
    /// acquisitions. Existing library entries are preserved as history.
    /// </remarks>
    [HttpGet("{id:guid}", Name = GetByIdRouteName)]
    [AllowAnonymous]
    [ProducesResponseType<GameResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var game = await getGameHandler.HandleAsync(id, cancellationToken);

        return game is null
            ? NotFound(ApiErrors.ResourceNotFound.ToProblemDetails(HttpContext.Request.Path))
            : Ok(ToResponse(game));
    }

    /// <summary>
    /// Creates a game as an administrator.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = IdentityAuthorizationPolicy.AdminOnly)]
    [ProducesResponseType<GameResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        CreateGameRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var actorUserId))
        {
            return Unauthorized(
                ApiErrors.Unauthenticated.ToProblemDetails(HttpContext.Request.Path));
        }

        var game = await createGameHandler.HandleAsync(
            new CreateGameCommand(
                actorUserId,
                request.Title,
                request.Description,
                request.BasePrice!.Value),
            cancellationToken);

        var response = ToResponse(game);

        logger.LogInformation(
            "GameCreated {ActorUserId} {TargetGameId} {TraceId}",
            actorUserId,
            game.Id,
            TraceIdentity.Resolve(HttpContext));

        return CreatedAtRoute(GetByIdRouteName, new { id = game.Id }, response);
    }

    private static GameResponse ToResponse(CatalogGameSummary game) =>
        new(
            game.Id,
            game.Title,
            game.Description,
            game.BasePrice,
            game.CurrentPrice,
            game.DiscountPercentage,
            game.IsActive);
}
