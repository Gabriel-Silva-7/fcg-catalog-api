using Xunit;
using FCG.Catalog.Domain.Catalog;

namespace FCG.Catalog.Domain.Tests.Catalog;

public sealed class GameTests
{
    private static readonly Guid ValidCreatorId =
        Guid.Parse("225e3332-44a9-41e0-b06a-73807dd2d2e4");

    private static readonly DateTime ValidCreatedAtUtc = DateTime.UnixEpoch;

    [Fact]
    public void Create_WhenDataIsValid_CreatesActiveGame()
    {
        var game = Game.Create(
            "The Witcher 3",
            "Open-world role-playing game",
            59.90m,
            ValidCreatorId,
            ValidCreatedAtUtc);

        Assert.NotEqual(Guid.Empty, game.Id);
        Assert.Equal("The Witcher 3", game.Title);
        Assert.Equal("Open-world role-playing game", game.Description);
        Assert.Equal(59.90m, game.BasePrice);
        Assert.True(game.IsActive);
        Assert.Equal(ValidCreatorId, game.CreatedByUserId);
        Assert.Equal(ValidCreatedAtUtc, game.CreatedAtUtc);
    }

    [Fact]
    public void Create_TrimsTitleAndDescription()
    {
        var game = CreateGame(
            title: "  The Witcher 3  ",
            description: "  Open-world role-playing game  ");

        Assert.Equal("The Witcher 3", game.Title);
        Assert.Equal("Open-world role-playing game", game.Description);
    }

    [Fact]
    public void Create_WhenTitleIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => CreateGame(title: null!));

        Assert.Equal("title", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WhenTitleIsEmpty_ThrowsArgumentException(string title)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateGame(title: title));

        Assert.Equal("title", exception.ParamName);
    }

    [Fact]
    public void Create_WhenTitleHasMaximumLength_AcceptsTitle()
    {
        var title = new string('a', Game.MaxTitleLength);

        var game = CreateGame(title: title);

        Assert.Equal(title, game.Title);
    }

    [Fact]
    public void Create_WhenTitleExceedsMaximumLength_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateGame(title: new string('a', Game.MaxTitleLength + 1)));

        Assert.Equal("title", exception.ParamName);
    }

    [Fact]
    public void Create_WhenDescriptionIsNull_AcceptsDescription()
    {
        var game = CreateGame(description: null);

        Assert.Null(game.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WhenDescriptionIsEmpty_NormalizesToNull(string description)
    {
        var game = CreateGame(description: description);

        Assert.Null(game.Description);
    }

    [Fact]
    public void Create_WhenDescriptionHasMaximumLength_AcceptsDescription()
    {
        var description = new string('a', Game.MaxDescriptionLength);

        var game = CreateGame(description: description);

        Assert.Equal(description, game.Description);
    }

    [Fact]
    public void Create_WhenDescriptionExceedsMaximumLength_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateGame(
                description: new string('a', Game.MaxDescriptionLength + 1)));

        Assert.Equal("description", exception.ParamName);
    }

    [Fact]
    public void Create_WhenBasePriceIsNegative_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateGame(basePrice: -0.01m));

        Assert.Equal("basePrice", exception.ParamName);
    }

    [Fact]
    public void Create_WhenBasePriceIsZero_AcceptsFreeGame()
    {
        var game = CreateGame(basePrice: 0m);

        Assert.Equal(0m, game.BasePrice);
    }

    [Fact]
    public void Create_WhenBasePriceIsFarAboveAnyRealisticGamePrice_AcceptsPrice()
    {
        var highPrice = 999_999.99m;

        var game = CreateGame(basePrice: highPrice);

        Assert.Equal(highPrice, game.BasePrice);
    }

    [Fact]
    public void Create_WhenBasePriceHasTwoDecimalPlaces_AcceptsPrice()
    {
        var game = CreateGame(basePrice: 19.99m);

        Assert.Equal(19.99m, game.BasePrice);
    }

    [Fact]
    public void Create_WhenBasePriceHasMoreThanTwoDecimalPlaces_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateGame(basePrice: 19.999m));

        Assert.Equal("basePrice", exception.ParamName);
    }

    [Fact]
    public void Create_WhenCreatorIdentifierIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateGame(createdByUserId: Guid.Empty));

        Assert.Equal("createdByUserId", exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Create_WhenCreationDateIsNotUtc_ThrowsArgumentException(DateTimeKind kind)
    {
        var createdAt = DateTime.SpecifyKind(ValidCreatedAtUtc, kind);

        var exception = Assert.Throws<ArgumentException>(
            () => CreateGame(createdAtUtc: createdAt));

        Assert.Equal("createdAtUtc", exception.ParamName);
    }

    [Fact]
    public void Deactivate_WhenGameIsActive_DeactivatesGame()
    {
        var game = CreateGame();

        game.Deactivate();

        Assert.False(game.IsActive);
    }

    [Fact]
    public void Deactivate_WhenGameIsAlreadyInactive_RemainsInactive()
    {
        var game = CreateGame();
        game.Deactivate();

        game.Deactivate();

        Assert.False(game.IsActive);
    }

    [Fact]
    public void Create_WhenTitleAlreadyExists_AllowsAnotherGameWithSameTitle()
    {
        var first = CreateGame(title: "Shared title");
        var second = CreateGame(title: "Shared title");

        Assert.Equal(first.Title, second.Title);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Game_HasNoPublicConstructor()
    {
        Assert.Empty(typeof(Game).GetConstructors());
    }

    [Fact]
    public void Game_PropertiesHaveNoPublicSetters()
    {
        Assert.All(
            typeof(Game).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }

    private static Game CreateGame(
        string title = "The Witcher 3",
        string? description = "Open-world role-playing game",
        decimal basePrice = 59.90m,
        Guid? createdByUserId = null,
        DateTime? createdAtUtc = null) =>
        Game.Create(
            title,
            description,
            basePrice,
            createdByUserId ?? ValidCreatorId,
            createdAtUtc ?? ValidCreatedAtUtc);
}
