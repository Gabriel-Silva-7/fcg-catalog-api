namespace FCG.Catalog.Api.Contracts;

public sealed record LibraryItemResponse(
    Guid GameId,
    string Title,
    DateTime AcquiredAt,
    decimal AcquisitionPrice);
