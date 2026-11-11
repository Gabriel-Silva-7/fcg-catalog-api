namespace FCG.Catalog.Application.Library;

public sealed record LibraryItem(
    Guid GameId,
    string Title,
    DateTime AcquiredAtUtc,
    decimal AcquisitionPrice);
