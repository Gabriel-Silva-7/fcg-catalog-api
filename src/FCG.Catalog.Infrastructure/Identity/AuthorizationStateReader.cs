using FCG.Catalog.Application.Identity;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Identity;

public sealed class AuthorizationStateReader(CatalogDbContext db) : IAuthorizationStateReader
{
    public Task<AuthorizationState?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.UserAuthorizationStates.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new AuthorizationState(x.UserId, x.EventVersion, x.Role, x.State)).SingleOrDefaultAsync(cancellationToken);
}
