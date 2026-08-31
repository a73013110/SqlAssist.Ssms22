using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetDefaultsTests
{
    [Fact]
    public void 內建JSON有四十筆且識別碼與捷徑唯一()
    {
        var defaults = SqlSnippetDefaults.Current;

        Assert.Equal(40, defaults.Count);
        Assert.Equal(
            defaults.Count,
            defaults.Snippets.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            defaults.Count,
            defaults.Snippets.Select(item => item.Shortcut).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(defaults.Snippets, snippet =>
        {
            Assert.True(SqlSnippetIdentity.IsValid(snippet.Id), snippet.Id);
            Assert.True(SqlSnippetLibrary.Empty.ValidateShortcut(snippet.Shortcut, null, out var error), error);
            Assert.False(SqlKeywordCatalog.TryGetCanonical(snippet.Shortcut, out _), snippet.Shortcut);
            Assert.DoesNotContain("$CURSOR$", snippet.Code, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 內建佔位符由程式碼順序推導且Tab模式至少有一欄()
    {
        foreach (var snippet in SqlSnippetDefaults.Current.Snippets)
        {
            var extracted = SqlSnippetPlaceholders.Extract(snippet.Code);

            Assert.Equal(extracted, snippet.Placeholders.Select(item => item.Id).ToArray());
            Assert.True(Count(snippet.Code, SqlSnippet.CaretMarker) <= 1, snippet.Shortcut);

            if (snippet.ExpansionMode == SqlSnippetExpansionMode.TabStops)
            {
                Assert.NotEmpty(snippet.Placeholders);
            }
        }
    }

    [Fact]
    public void CASE捷徑不與關鍵字撞名()
    {
        Assert.False(SqlSnippetDefaults.Current.TryGet("case", out _));
        Assert.True(SqlSnippetDefaults.Current.TryGet("cs", out _));
    }

    [Fact]
    public void 沒有前綴時Snippet不會佔據第一順位()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[]
            {
                new SqlSuggestion(
                    "CopyNo",
                    "[CopyNo]",
                    "INT",
                    "CopyNo",
                    SuggestionKind.Column)
            });

        var first = candidates
            .OrderByDescending(SuggestionMatcher.ComposeStandingScore)
            .First();

        Assert.NotEqual(SuggestionKind.Snippet, first.Kind);
    }

    [Fact]
    public void 最近使用過的Snippet仍不佔據空前綴首頁()
    {
        SqlSuggestionUsage.Clear();

        try
        {
            var snippet = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
                .Single(item => item.DisplayText == "ssf");
            var column = new SqlSuggestion("CopyNo", "CopyNo", "INT", "CopyNo", SuggestionKind.Column);
            SqlSuggestionUsage.Record(snippet);

            Assert.True(
                SuggestionMatcher.ComposeStandingScore(column) >
                SuggestionMatcher.ComposeStandingScore(snippet));
        }
        finally
        {
            SqlSuggestionUsage.Clear();
        }
    }

    [Fact]
    public void 輸入libr時資料表排在任何Snippet之前()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[]
            {
                new SqlSuggestion(
                    "Lib_Reader",
                    "[dbo].[Lib_Reader]",
                    "Table · dbo",
                    "Table Lib_Reader",
                    SuggestionKind.Table,
                    schemaName: "dbo")
            });

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("libr"));

        Assert.Equal("Lib_Reader", ranked[0].Suggestion.DisplayText);
    }

    [Fact]
    public void 危險片段只在空前綴首頁隱藏()
    {
        var destructive = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Single(item => item.DisplayText == "df");

        Assert.False(SuggestionMatcher.IsVisibleWithoutPrefix(destructive, categorySelected: false));
        Assert.True(SuggestionMatcher.IsVisibleWithoutPrefix(destructive, categorySelected: true));
    }

    [Fact]
    public void DDL片段不會混進SELECT欄位位置()
    {
        var filtered = SuggestionMatcher.Filter(
            BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current),
            SqlCompletionContextAnalyzer.Analyze("SELECT "));

        Assert.DoesNotContain(filtered, item => item.DisplayText == "ctb");
        Assert.Contains(filtered, item => item.DisplayText == "cs");
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var offset = 0;

        while ((offset = text.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
