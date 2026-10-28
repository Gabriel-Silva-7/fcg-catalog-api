using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.WebUtilities;

namespace FCG.Catalog.Api.Errors;

public static class ApiErrors
{
    public static readonly ApiError ValidationError = new("validation_error", 400, "Validation failed");
    public static readonly ApiError Unauthenticated = new("unauthenticated", 401, "Authentication required");
    public static readonly ApiError Forbidden = new("forbidden", 403, "Access denied");
    public static readonly ApiError ResourceNotFound = new("resource_not_found", 404, "Resource not found");
    public static readonly ApiError GameAlreadyAcquired = new("game_already_acquired", 409, "Game already acquired");
    public static readonly ApiError OrderPlacementUnavailable = new("order_placement_unavailable", 503, "Order placement temporarily unavailable");
    public static readonly ApiError InternalError = new("internal_error", 500, "Unexpected error");
    public static IReadOnlyCollection<ApiError> All { get; } = [ValidationError, Unauthenticated, Forbidden, ResourceNotFound, GameAlreadyAcquired, OrderPlacementUnavailable, InternalError];
    public static bool TryGetByCode(string? code, [NotNullWhen(true)] out ApiError? error)
    {
        error = All.FirstOrDefault(error => error.Code == code);
        return error is not null;
    }
    public static ApiError ForStatus(int status) => All.FirstOrDefault(error => error.Status == status)
        ?? new ApiError($"http_{status}", status, ReasonPhrases.GetReasonPhrase(status));
}
