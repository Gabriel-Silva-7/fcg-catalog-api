using FCG.Catalog.Domain.Library;
using Xunit;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class LibraryEntryTests
{
    private static readonly Guid ValidUserId =
        Guid.Parse("135a28ec-fb69-40eb-bccb-f9de2ee85e3e");

    private static readonly Guid ValidGameId =
        Guid.Parse("e9bd4d3e-aa24-4eb6-86b1-96d9f48522a0");

    private static readonly DateTime ValidAcquiredAtUtc = DateTime.UnixEpoch;

    [Fact]
    public void Create_WhenDataIsValid_PreservesAcquisitionSnapshot()
    {
        var entry = CreateEntry();

        Assert.Equal(ValidUserId, entry.UserId);
        Assert.Equal(ValidGameId, entry.GameId);
        Assert.Equal(ValidAcquiredAtUtc, entry.AcquiredAtUtc);
        Assert.Equal(59.90m, entry.AcquisitionPrice);
    }

    [Fact]
    public void Create_WhenUserIdentifierIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateEntry(userId: Guid.Empty));

        Assert.Equal("userId", exception.ParamName);
    }

    [Fact]
    public void Create_WhenGameIdentifierIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateEntry(gameId: Guid.Empty));

        Assert.Equal("gameId", exception.ParamName);
    }

    [Fact]
    public void Create_WhenAcquisitionPriceIsNegative_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateEntry(acquisitionPrice: -0.01m));

        Assert.Equal("acquisitionPrice", exception.ParamName);
    }

    [Fact]
    public void Create_WhenAcquisitionPriceIsZero_AcceptsFreeAcquisition()
    {
        var entry = CreateEntry(acquisitionPrice: 0m);

        Assert.Equal(0m, entry.AcquisitionPrice);
    }

    [Fact]
    public void Create_WhenAcquisitionPriceIsFarAboveAnyRealisticGamePrice_AcceptsPrice()
    {
        var highPrice = 999_999.99m;

        var entry = CreateEntry(acquisitionPrice: highPrice);

        Assert.Equal(highPrice, entry.AcquisitionPrice);
    }

    [Fact]
    public void Create_WhenAcquisitionPriceHasMoreThanTwoDecimalPlaces_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateEntry(acquisitionPrice: 59.999m));

        Assert.Equal("acquisitionPrice", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Create_WhenAcquisitionDateIsNotUtc_ThrowsArgumentException(DateTimeKind kind)
    {
        var acquiredAt = DateTime.SpecifyKind(ValidAcquiredAtUtc, kind);

        var exception = Assert.Throws<ArgumentException>(
            () => CreateEntry(acquiredAtUtc: acquiredAt));

        Assert.Equal("acquiredAtUtc", exception.ParamName);
    }

    [Fact]
    public void LibraryEntry_HasNoPublicConstructor()
    {
        Assert.Empty(typeof(LibraryEntry).GetConstructors());
    }

    [Fact]
    public void LibraryEntry_PropertiesHaveNoPublicSetters()
    {
        Assert.All(
            typeof(LibraryEntry).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }

    [Fact]
    public void LibraryEntry_ExposesOnlyExpectedScalarProperties()
    {
        var propertyNames = typeof(LibraryEntry)
            .GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            ["AcquiredAtUtc", "AcquisitionPrice", "GameId", "OriginOrderId", "UserId"],
            propertyNames);
    }

    private static LibraryEntry CreateEntry(
        Guid? userId = null,
        Guid? gameId = null,
        DateTime? acquiredAtUtc = null,
        decimal acquisitionPrice = 59.90m) =>
        LibraryEntry.Create(
            userId ?? ValidUserId,
            gameId ?? ValidGameId,
            acquiredAtUtc ?? ValidAcquiredAtUtc,
            acquisitionPrice);
}
