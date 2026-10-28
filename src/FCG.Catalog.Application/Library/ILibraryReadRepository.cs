using FCG.Catalog.Application.Common;

namespace FCG.Catalog.Application.Library;

public interface ILibraryReadRepository
{
    Task<PagedResult<LibraryItem>> SearchByUserAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);
}
