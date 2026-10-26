using FCG.Catalog.Application.Common;
namespace FCG.Catalog.Infrastructure.Catalog;
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
