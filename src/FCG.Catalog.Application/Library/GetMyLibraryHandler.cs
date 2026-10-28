using FCG.Catalog.Application.Common;

namespace FCG.Catalog.Application.Library;

public sealed record GetMyLibraryQuery(Guid UserId, int Page, int PageSize)
{
    public const int MinPage = 1;

    // Mantém (page - 1) * pageSize dentro do intervalo de int usado pelo Skip.
    public const int MaxPage = 1_000_000;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;
}

public sealed class GetMyLibraryHandler(ILibraryReadRepository libraryRepository)
{
    public Task<PagedResult<LibraryItem>> HandleAsync(
        GetMyLibraryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Validate(query);

        return libraryRepository.SearchByUserAsync(
            query.UserId,
            query.Page,
            query.PageSize,
            cancellationToken);
    }

    private static void Validate(GetMyLibraryQuery query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (query.Page is < GetMyLibraryQuery.MinPage or > GetMyLibraryQuery.MaxPage)
        {
            errors[nameof(query.Page)] =
                [$"Page must be between {GetMyLibraryQuery.MinPage} and {GetMyLibraryQuery.MaxPage}."];
        }

        if (query.PageSize is < GetMyLibraryQuery.MinPageSize or > GetMyLibraryQuery.MaxPageSize)
        {
            errors[nameof(query.PageSize)] =
                [$"Page size must be between {GetMyLibraryQuery.MinPageSize} and {GetMyLibraryQuery.MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }
    }
}
