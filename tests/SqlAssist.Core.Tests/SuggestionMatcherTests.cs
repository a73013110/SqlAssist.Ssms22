using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SuggestionMatcherTests
{
    private static SqlSuggestion Table(string name, string schema = "dbo")
    {
        return new SqlSuggestion(
            name,
            $"[{schema}].[{name}]",
            $"Table · {schema}",
            $"Table {name}",
            SuggestionKind.Table,
            schemaName: schema);
    }

    private static SqlSuggestion Procedure(string name, string schema = "dbo")
    {
        return new SqlSuggestion(
            name,
            $"[{schema}].[{name}]",
            $"Procedure · {schema}",
            $"Procedure {name}",
            SuggestionKind.Procedure,
            schemaName: schema);
    }

    private static SqlSuggestion Column(string name)
    {
        return new SqlSuggestion(
            name,
            name,
            "varchar(20) NOT NULL",
            name,
            SuggestionKind.Column);
    }

    /// <summary>
    /// 沒有限定字的位置也要看得到欄位：SELECT | FROM PUBLISHER a 這種情形，
    /// 使用者要的幾乎都是欄位而不是整個資料庫的物件清單。
    /// </summary>
    [Fact]
    public void 沒有限定字時仍建議欄位()
    {
        var names = RankNames(
            new[] { Table("PUBL_Log"), Column("PUBL_CODE") },
            "SELECT publ");

        Assert.Contains("PUBL_CODE", names);
    }

    [Fact]
    public void 分數相同時欄位排在資料表之前()
    {
        var names = RankNames(
            new[] { Table("PUBLCODE"), Column("PUBLCODE") },
            "SELECT publcode");

        Assert.Equal("PUBLCODE", names[0]);
        Assert.Equal(SuggestionKind.Column, SuggestionMatcher
            .Rank(new[] { Table("PUBLCODE"), Column("PUBLCODE") },
                SqlCompletionContextAnalyzer.Analyze("SELECT publcode"))[0]
            .Suggestion.Kind);
    }

    /// <summary>
    /// FROM 之後只能是資料來源，欄位不該出現在那裡。
    /// </summary>
    [Fact]
    public void 資料來源位置不建議欄位()
    {
        var names = RankNames(
            new[] { Table("PUBLISHER"), Column("PUBL_CODE") },
            "SELECT * FROM publ");

        Assert.Equal(new[] { "PUBLISHER" }, names);
    }

    private static IReadOnlyList<string> RankNames(
        IEnumerable<SqlSuggestion> suggestions,
        string textBeforeCaret)
    {
        return SuggestionMatcher
            .Rank(suggestions, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret))
            .Select(item => item.Suggestion.DisplayText)
            .ToArray();
    }

    /// <summary>
    /// 使用者回報的情境：輸入 libr 時，Lib_Reader 必須是第一順位，
    /// 不必打到 lib_re 才浮上來。
    /// </summary>
    [Fact]
    public void 輸入libr時Lib_Reader排第一()
    {
        var candidates = new[]
        {
            Table("Lib_Reader"),
            Table("MyLibrTable"),
            Table("Lib_ReaderTag"),
            Table("LibBackupRecord"),
            Table("Lib_Tag")
        };

        var ranked = RankNames(candidates, "SELECT * FROM libr");

        Assert.Equal("Lib_Reader", ranked[0]);
        Assert.DoesNotContain("Lib_Tag", ranked);
    }

    [Fact]
    public void 完全相同的輸入一定排第一()
    {
        var candidates = BuiltInSuggestionCatalog.Create()
            .Concat(new[] { Table("ssf_Archive"), Table("ssfLog") })
            .ToArray();

        var ranked = RankNames(candidates, "ssf");

        Assert.Equal("ssf", ranked[0]);
    }

    [Fact]
    public void 分數相同時較短的名稱排前面()
    {
        var candidates = new[] { Table("OrderDetailHistory"), Table("Order") };

        var ranked = RankNames(candidates, "SELECT * FROM order");

        Assert.Equal("Order", ranked[0]);
    }

    [Fact]
    public void FROM之後只顯示資料表與View()
    {
        var candidates = BuiltInSuggestionCatalog.Create()
            .Concat(new[] { Table("Publisher"), Procedure("usp_Publisher") })
            .ToArray();

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM "));

        Assert.NotEmpty(ranked);
        Assert.All(ranked, item => Assert.Equal(SuggestionKind.Table, item.Suggestion.Kind));
    }

    [Fact]
    public void ALTER_PROCEDURE之後只顯示Procedure()
    {
        var candidates = new[] { Table("Publisher"), Procedure("usp_Publisher") };

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("ALTER PROCEDURE "));

        Assert.Single(ranked);
        Assert.Equal(SuggestionKind.Procedure, ranked[0].Suggestion.Kind);
    }

    [Fact]
    public void Schema限定後只顯示該Schema的物件()
    {
        var candidates = new[] { Table("Publisher", "dbo"), Table("Publisher", "sales") };

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM [sales]."));

        Assert.Single(ranked);
        Assert.Equal("sales", ranked[0].Suggestion.SchemaName);
    }

    [Fact]
    public void 回傳結果帶有可供高亮的命中區段()
    {
        var ranked = SuggestionMatcher.Rank(
            new[] { Table("Lib_Reader") },
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr"));

        var match = Assert.Single(ranked);
        Assert.Equal(2, match.Spans.Count);
        Assert.Equal(0, match.Spans[0].Start);
        Assert.Equal(3, match.Spans[0].Length);
        Assert.Equal(4, match.Spans[1].Start);
        Assert.Equal(1, match.Spans[1].Length);
    }

    [Fact]
    public void 遵守數量上限且保留分數最高的項目()
    {
        var candidates = Enumerable.Range(0, 50)
            .Select(index => Table($"Lib_Reader{index:D2}"))
            .Concat(new[] { Table("Lib_Reader") })
            .ToArray();

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr"),
            maximumCount: 5);

        Assert.Equal(5, ranked.Count);
        Assert.Equal("Lib_Reader", ranked[0].Suggestion.DisplayText);
    }

    [Fact]
    public void 分數由高到低排列()
    {
        var candidates = new[]
        {
            Table("Lib_Reader"),
            Table("LibBackupRecord"),
            Table("MyLibrTable")
        };

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM libr"));

        var scores = ranked.Select(item => item.Score).ToArray();
        Assert.Equal(scores.OrderByDescending(score => score), scores);
    }

    [Fact]
    public void 上下文無效時回傳空清單()
    {
        var ranked = SuggestionMatcher.Rank(
            new[] { Table("Publisher") },
            SqlCompletionContextAnalyzer.Analyze("-- 註解 publ"));

        Assert.Empty(ranked);
    }

    [Fact]
    public void 輸入單一字母時關鍵字與Snippet排在資料表之前()
    {
        var candidates = BuiltInSuggestionCatalog.Create()
            .Concat(new[] { Table("Lib_Reader") })
            .ToArray();

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("s"));

        var topKinds = ranked.Take(3).Select(item => item.Suggestion.Kind).ToArray();
        Assert.All(topKinds, kind =>
            Assert.True(kind == SuggestionKind.Keyword || kind == SuggestionKind.Snippet));
    }
}
