using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Catalog;

namespace FCG.Catalog.Application.Catalog;

public sealed record ListGamesQuery(
    string? Search,
    int Page,
    int PageSize,
    string? SortBy)
{
    public const int MinPage = 1;

    // Mantém (page - 1) * pageSize dentro do intervalo de int usado pelo Skip.
    public const int MaxPage = 1_000_000;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;
}

public sealed class ListGamesHandler(IGameRepository gameRepository)
{
    public async Task<PagedResult<CatalogGameSummary>> HandleAsync(
        ListGamesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sortField = Validate(query);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var result = await gameRepository.SearchActiveAsync(
            search,
            sortField,
            query.Page,
            query.PageSize,
            cancellationToken);

        return new PagedResult<CatalogGameSummary>(
            result.Items.Select(CatalogGameMapper.WithPricing).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    private static GameSortField Validate(ListGamesQuery query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (query.Page is < ListGamesQuery.MinPage or > ListGamesQuery.MaxPage)
        {
            errors[nameof(query.Page)] =
                [$"Page must be between {ListGamesQuery.MinPage} and {ListGamesQuery.MaxPage}."];
        }

        if (query.PageSize is < ListGamesQuery.MinPageSize or > ListGamesQuery.MaxPageSize)
        {
            errors[nameof(query.PageSize)] =
                [$"Page size must be between {ListGamesQuery.MinPageSize} and {ListGamesQuery.MaxPageSize}."];
        }

        if (query.Search?.Length > Game.MaxTitleLength)
        {
            errors[nameof(query.Search)] =
                [$"Search cannot exceed {Game.MaxTitleLength} characters."];
        }

        if (!GameSortFields.TryParse(query.SortBy, out var sortField))
        {
            errors[nameof(query.SortBy)] =
                [$"Sort field must be one of: {GameSortFields.Title}, {GameSortFields.BasePrice}, {GameSortFields.CreatedAt}."];
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }

        return sortField;
    }
}
