using System.ComponentModel.DataAnnotations;
using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Contracts;

public sealed record ListGamesRequest
{
    [FromQuery(Name = "page")]
    [Range(ListGamesQuery.MinPage, ListGamesQuery.MaxPage)]
    public int Page { get; init; } = ListGamesQuery.MinPage;

    [FromQuery(Name = "pageSize")]
    [Range(ListGamesQuery.MinPageSize, ListGamesQuery.MaxPageSize)]
    public int PageSize { get; init; } = ListGamesQuery.DefaultPageSize;

    [FromQuery(Name = "search")]
    [StringLength(Game.MaxTitleLength)]
    public string? Search { get; init; }

    [FromQuery(Name = "sortBy")]
    [RegularExpression(GameSortFields.Pattern)]
    public string? SortBy { get; init; }
}
