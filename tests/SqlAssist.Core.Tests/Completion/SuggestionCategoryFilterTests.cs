using System;
using System.Linq;
using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

public sealed class SuggestionCategoryFilterTests
{
    private static readonly SuggestionCategorySet TablesAndViews =
        SuggestionCategorySet.Of(SuggestionCategory.Table, SuggestionCategory.View);

    [Fact]
    public void EverySuggestionKindHasCategory()
    {
        foreach (SuggestionKind kind in Enum.GetValues(typeof(SuggestionKind)))
        {
            _ = SuggestionCategories.Of(kind);
        }
    }

    [Theory]
    [InlineData(SuggestionKind.ScriptDataSource, SuggestionCategory.Table)]
    [InlineData(SuggestionKind.Schema, SuggestionCategory.SchemaOrDatabase)]
    [InlineData(SuggestionKind.Database, SuggestionCategory.SchemaOrDatabase)]
    [InlineData(SuggestionKind.LinkedServer, SuggestionCategory.SchemaOrDatabase)]
    [InlineData(SuggestionKind.Sequence, SuggestionCategory.Sequence)]
    [InlineData(SuggestionKind.Variable, null)]
    [InlineData(SuggestionKind.DataType, null)]
    public void MapsKindToCategory(SuggestionKind kind, SuggestionCategory? expected)
    {
        Assert.Equal(expected, SuggestionCategories.Of(kind));
    }

    [Fact]
    public void SingleCategoryHasNoButtons()
    {
        Assert.Empty(SuggestionCategoryFilter.Buttons(SuggestionCategorySet.Of(SuggestionCategory.Column)));
    }

    [Fact]
    public void ButtonsFollowDeclarationOrder()
    {
        var present = SuggestionCategorySet.Of(
            SuggestionCategory.SchemaOrDatabase,
            SuggestionCategory.Sequence,
            SuggestionCategory.TableFunction,
            SuggestionCategory.Table);

        Assert.Equal(
            new[]
            {
                SuggestionCategory.Table,
                SuggestionCategory.TableFunction,
                SuggestionCategory.Sequence,
                SuggestionCategory.SchemaOrDatabase
            },
            SuggestionCategoryFilter.Buttons(present));
    }

    [Fact]
    public void ButtonsStopAtTen()
    {
        var all = Enum.GetValues(typeof(SuggestionCategory))
            .Cast<SuggestionCategory>()
            .Aggregate(SuggestionCategorySet.Empty, (set, category) => set.With(category));

        var buttons = SuggestionCategoryFilter.Buttons(all);

        Assert.Equal(SuggestionCategoryFilter.MaximumButtons, buttons.Count);
        Assert.Equal(SuggestionCategory.Column, buttons[0]);
    }

    [Fact]
    public void NothingSelectedShowsEverything()
    {
        var applied = SuggestionCategoryFilter.Apply(SuggestionCategorySet.Empty, TablesAndViews);

        Assert.True(applied.IsEmpty);
        Assert.True(SuggestionCategoryFilter.Includes(applied, SuggestionCategory.Keyword));
    }

    [Fact]
    public void SelectedCategoryWithMatchesFilters()
    {
        var selected = SuggestionCategorySet.Of(SuggestionCategory.Table);

        var applied = SuggestionCategoryFilter.Apply(selected, TablesAndViews);

        Assert.Equal(selected, applied);
        Assert.False(SuggestionCategoryFilter.Includes(applied, SuggestionCategory.View));
    }

    [Fact]
    public void SelectedCategoryWithoutMatchesIsReleased()
    {
        var applied = SuggestionCategoryFilter.Apply(
            SuggestionCategorySet.Of(SuggestionCategory.Procedure),
            TablesAndViews);

        Assert.True(applied.IsEmpty);
    }

    [Fact]
    public void OnlyUnmatchedPartOfSelectionIsReleased()
    {
        var applied = SuggestionCategoryFilter.Apply(
            SuggestionCategorySet.Of(SuggestionCategory.Table, SuggestionCategory.Procedure),
            TablesAndViews);

        Assert.Equal(SuggestionCategorySet.Of(SuggestionCategory.Table), applied);
    }

    [Fact]
    public void SetCountsAndOrders()
    {
        var set = SuggestionCategorySet.Of(SuggestionCategory.Snippet, SuggestionCategory.Column, SuggestionCategory.Column);

        Assert.Equal(2, set.Count);
        Assert.Equal(new[] { SuggestionCategory.Column, SuggestionCategory.Snippet }, set.InOrder());
    }
}
