using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Catalog.Api.Contracts;

public sealed record ListOrdersRequest
{
    [FromQuery(Name = "page"), Range(1, 1_000_000)] public int Page { get; init; } = 1;
    [FromQuery(Name = "pageSize"), Range(1, 100)] public int PageSize { get; init; } = 20;
}
