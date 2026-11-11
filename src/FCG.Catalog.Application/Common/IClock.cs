namespace FCG.Catalog.Application.Common;

public interface IClock
{
    DateTime UtcNow { get; }
}
