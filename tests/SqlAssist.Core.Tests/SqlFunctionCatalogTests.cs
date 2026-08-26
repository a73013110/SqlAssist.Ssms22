using System;
using System.Linq;
using SqlAssist.Core;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests;

/// <summary>
/// T-SQL 內建函式的建議項。
/// </summary>
public sealed class SqlFunctionCatalogTests
{
    private static SqlSuggestion Get(string name) =>
        Assert.Single(
            SqlFunctionCatalog.All,
            item => string.Equals(item.DisplayText, name, StringComparison.Ordinal));

    [Theory]
    [InlineData("COUNT")]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("GETDATE")]
    [InlineData("ISNULL")]
    [InlineData("CAST")]
    [InlineData("ROW_NUMBER")]
    public void 常用函式都在清單裡(string name)
    {
        Assert.Equal(SuggestionKind.BuiltInFunction, Get(name).Kind);
    }

    /// <summary>
    /// 插入文字帶著左括號。
    /// </summary>
    /// <remarks>
    /// 這些名稱單獨出現一律是語法錯誤，補上括號等於少按一次鍵。
    /// </remarks>
    [Fact]
    public void 插入文字帶左括號()
    {
        Assert.Equal("COUNT(", Get("COUNT").InsertionText);
        Assert.Equal("GETDATE(", Get("GETDATE").InsertionText);
    }

    /// <summary>
    /// 與關鍵字重疊的名稱不收。
    /// </summary>
    /// <remarks>
    /// <c>LEFT</c> 同時是 <c>LEFT JOIN</c> 與 <c>LEFT(字串, 長度)</c>。收進來會讓它
    /// 只剩運算式位置，<c>LEFT JOIN</c> 就從清單裡消失了——少一個函式只是少一個補字，
    /// 少一個 JOIN 是使用者打不出來。
    /// </remarks>
    [Fact]
    public void 與關鍵字重疊的名稱不收()
    {
        var overlapping = SqlFunctionCatalog.All
            .Where(item => SqlKeywordCatalog.IsKeyword(item.DisplayText))
            .Select(item => item.DisplayText)
            .ToArray();

        Assert.Empty(overlapping);
    }

    /// <summary>
    /// 重疊排除是活的，不是一條永遠不會執行的保險。
    /// </summary>
    /// <remarks>
    /// 清單裡確實寫了 <c>LEFT</c>、<c>CONVERT</c> 這些名稱——它們是內建函式，
    /// 只是同時也是 ScriptDom 認得的關鍵字。哪些會被擋掉交給比對決定，
    /// 不由人記在腦子裡；換一版 SSMS 之後這一組就會自己變。
    /// </remarks>
    [Theory]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("CONVERT")]
    [InlineData("COALESCE")]
    [InlineData("NULLIF")]
    public void 同時是關鍵字的函式讓給關鍵字(string name)
    {
        Assert.True(SqlKeywordCatalog.IsKeyword(name));
        Assert.DoesNotContain(SqlFunctionCatalog.All, item => item.DisplayText == name);

        var catalog = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        Assert.Contains(catalog, item =>
            item.Kind == SuggestionKind.Keyword && item.DisplayText == name);
    }

    /// <summary>語句開頭與資料來源位置不該冒出 COUNT。</summary>
    [Fact]
    public void 只出現在運算式位置()
    {
        var positions = Get("COUNT").Positions;

        Assert.Equal(SqlKeywordPosition.None, positions & SqlKeywordPosition.StatementStart);
        Assert.Equal(SqlKeywordPosition.None, positions & SqlKeywordPosition.DataSource);
        Assert.Equal(SqlKeywordPosition.None, positions & SqlKeywordPosition.DdlObject);
        Assert.NotEqual(SqlKeywordPosition.None, positions & SqlKeywordPosition.SelectList);
        Assert.NotEqual(SqlKeywordPosition.None, positions & SqlKeywordPosition.Predicate);
    }

    [Fact]
    public void 內建函式會出現在候選清單裡()
    {
        var catalog = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        Assert.Contains(catalog, item =>
            item.Kind == SuggestionKind.BuiltInFunction && item.DisplayText == "COUNT");
    }

    /// <summary>
    /// SELECT 之後列得出 COUNT，FROM 之後列不出來。
    /// </summary>
    /// <remarks>
    /// 這一條走的是完整的過濾路徑，不是只看目錄裡的旗標——
    /// 位置分析器與目錄的旗標對不上的話，這裡才會發現。
    /// </remarks>
    [Fact]
    public void 依位置過濾內建函式()
    {
        var catalog = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        var inSelectList = SuggestionMatcher.Filter(
            catalog,
            SqlCompletionContextAnalyzer.Analyze("SELECT "));

        Assert.Contains(inSelectList, item => item.DisplayText == "COUNT");

        var afterFrom = SuggestionMatcher.Filter(
            catalog,
            SqlCompletionContextAnalyzer.Analyze("SELECT * FROM "));

        Assert.DoesNotContain(afterFrom, item => item.DisplayText == "COUNT");
    }

    /// <summary>
    /// ALTER FUNCTION 之後只列得出資料庫裡的函式。
    /// </summary>
    /// <remarks>
    /// 內建函式沒有定義可以改，出現在那裡只會讓使用者選到一個改不了的東西。
    /// </remarks>
    [Fact]
    public void ALTER_FUNCTION之後不列內建函式()
    {
        var catalog = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        var filtered = SuggestionMatcher.Filter(
            catalog,
            SqlCompletionContextAnalyzer.Analyze("ALTER FUNCTION "));

        Assert.DoesNotContain(filtered, item => item.Kind == SuggestionKind.BuiltInFunction);
    }
}
