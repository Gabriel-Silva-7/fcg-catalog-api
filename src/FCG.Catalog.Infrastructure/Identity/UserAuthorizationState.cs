using FCG.Contracts;

namespace FCG.Catalog.Infrastructure.Identity;

public sealed class UserAuthorizationState
{
    public Guid UserId { get; set; }
    public long EventVersion { get; set; }
    public IdentityRole Role { get; set; }
    public IdentityState State { get; set; }
}
