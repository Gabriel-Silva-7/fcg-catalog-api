using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FCG.Catalog.Api.Contracts;
using FCG.Catalog.Api.Errors;
using FCG.Catalog.Application.Library;
using FCG.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Controllers;

[ApiController]
[Route("api/v1/me/library")]
[Authorize(Policy = IdentityAuthorizationPolicy.UserOrAdmin)]
public sealed class LibraryController(GetMyLibraryHandler handler) : ControllerBase
{
    /// <summary>Lists only the caller's acquired games, including inactive games.</summary>
    /// <remarks>AcquisitionPrice is historical; catalog price changes do not rewrite it.</remarks>
    [HttpGet]
    [ProducesResponseType<PagedResponse<LibraryItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] ListLibraryRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            return Unauthorized(ApiErrors.Unauthenticated.ToProblemDetails(Request.Path));
        var result = await handler.HandleAsync(new GetMyLibraryQuery(userId, request.Page, request.PageSize), cancellationToken);
        return Ok(new PagedResponse<LibraryItemResponse>(result.Items.Select(x =>
            new LibraryItemResponse(x.GameId, x.Title, x.AcquiredAtUtc, x.AcquisitionPrice)).ToArray(), result.Page, result.PageSize, result.TotalCount));
    }
}
