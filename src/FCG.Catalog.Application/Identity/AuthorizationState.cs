using FCG.Contracts;

namespace FCG.Catalog.Application.Identity;

public sealed record AuthorizationState(Guid UserId, long EventVersion, IdentityRole Role, IdentityState State);
public interface IAuthorizationStateReader
{
    Task<AuthorizationState?> FindAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Authorization decisions over the fields Catalog owns, without fabricating retained contact data.</summary>
public static class CatalogAuthorization
{
    public static AuthorizationDecision Decide(TokenClaims token, AuthorizationState? current, bool stateAvailable)
    {
        if (!Guid.TryParse(token.Subject, out var id) || id == Guid.Empty || !Guid.TryParse(token.JwtId, out var jwtId)
            || jwtId == Guid.Empty || token.Role is not ("User" or "Administrator")) return new(401);
        if (!stateAvailable || current is null || current.EventVersion <= 0 || !Enum.IsDefined(current.Role) || !Enum.IsDefined(current.State))
            return new(503, "auth_state_unavailable", 1);
        return current.UserId != id || current.State != IdentityState.Active ? new(401) : new(200);
    }
}
