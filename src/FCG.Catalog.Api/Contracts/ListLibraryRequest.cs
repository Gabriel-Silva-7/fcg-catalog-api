using System.ComponentModel.DataAnnotations;
using FCG.Catalog.Application.Library;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Contracts;

public sealed record ListLibraryRequest
{
    [FromQuery(Name = "page")]
    [Range(GetMyLibraryQuery.MinPage, GetMyLibraryQuery.MaxPage)]
    public int Page { get; init; } = GetMyLibraryQuery.MinPage;

    [FromQuery(Name = "pageSize")]
    [Range(GetMyLibraryQuery.MinPageSize, GetMyLibraryQuery.MaxPageSize)]
    public int PageSize { get; init; } = GetMyLibraryQuery.DefaultPageSize;
}
