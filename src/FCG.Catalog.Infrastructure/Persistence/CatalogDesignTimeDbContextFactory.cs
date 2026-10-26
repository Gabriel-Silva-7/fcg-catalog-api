using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FCG.Catalog.Infrastructure.Persistence;

public sealed class CatalogDesignTimeDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Database=fcg_catalog;Username=fcg_catalog;Password=design-time-only";
        return new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connection).Options);
    }
}
