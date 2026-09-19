using System;
using System.Linq;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;
using SqlAssist.Ssms22.Search;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

public sealed class SqlSearchDefinitionHighlightTests
{
    private const string TableScript =
        "-- 物件：[dbo].[Loan]\r\n" +
        "SET ANSI_NULLS ON;\r\n" +
        "CREATE TABLE [dbo].[Loan]\r\n" +
        "(\r\n" +
        "    [LoanId] int NOT NULL,\r\n" +
        "    [CopyNo] int NOT NULL\r\n" +
        ");\r\n";

    [Fact]
    public void 資料行命中落在那一欄的定義上()
    {
        var hit = Hit(SearchMatchTarget.Column, "CopyNo", new MatchSpan(0, 6));

        var spans = SqlSearchDefinitionHighlight.Locate(hit, TableScript);

        var span = Assert.Single(spans);
        Assert.Equal("CopyNo", TableScript.Substring(span.Start, span.Length));
        // 落在資料行那一行，不是檔頭註解那一行。
        Assert.Contains("[CopyNo]", LineAt(TableScript, span.Start), StringComparison.Ordinal);
    }

    /// <remarks>
    /// 檔頭註解裡也有物件名稱；停在那裡等於把使用者帶到一行註解上，而他要看的是 CREATE。
    /// </remarks>
    [Fact]
    public void 名稱命中跳過檔頭註解落在CREATE那一行()
    {
        var hit = Hit(SearchMatchTarget.Name, "Loan", new MatchSpan(0, 4));

        var span = Assert.Single(SqlSearchDefinitionHighlight.Locate(hit, TableScript));

        Assert.StartsWith("CREATE TABLE", LineAt(TableScript, span.Start), StringComparison.Ordinal);
    }

    [Fact]
    public void 名稱命中只有部分字元時只標那幾個字()
    {
        var hit = Hit(SearchMatchTarget.Name, "Loan", new MatchSpan(0, 2));

        var span = Assert.Single(SqlSearchDefinitionHighlight.Locate(hit, TableScript));

        Assert.Equal("Lo", TableScript.Substring(span.Start, span.Length));
    }

    [Fact]
    public void 本文命中落在定義裡的那一行()
    {
        const string script =
            "-- 物件：[dbo].[usp_GetLoan]\r\n" +
            "ALTER PROCEDURE [dbo].[usp_GetLoan]\r\n" +
            "AS\r\n" +
            "    SELECT CopyNo FROM dbo.Loan WHERE LoanId = @LoanId;\r\n";
        var hit = Hit(SearchMatchTarget.Text, "    SELECT CopyNo FROM dbo.Loan WHERE LoanId = @LoanId;", new MatchSpan(11, 6));

        var span = Assert.Single(SqlSearchDefinitionHighlight.Locate(hit, script));

        Assert.Equal("CopyNo", script.Substring(span.Start, span.Length));
    }

    /// <remarks>
    /// 定義自己以註解開頭時，跳過檔頭會連命中那一行一起跳掉；本文命中因此從頭找。
    /// </remarks>
    [Fact]
    public void 本文命中認得定義開頭的註解()
    {
        const string script = "-- 借閱明細：Loan\r\nALTER VIEW [dbo].[v_Loan]\r\n";
        var hit = Hit(SearchMatchTarget.Text, "-- 借閱明細：Loan", new MatchSpan(8, 4));

        var span = Assert.Single(SqlSearchDefinitionHighlight.Locate(hit, script));

        Assert.Equal("Loan", script.Substring(span.Start, span.Length));
    }

    [Fact]
    public void 對不上時整組放棄不猜位置()
    {
        var hit = Hit(SearchMatchTarget.Text, "SELECT * FROM dbo.Branch;", new MatchSpan(18, 6));

        Assert.Empty(SqlSearchDefinitionHighlight.Locate(hit, TableScript));
    }

    [Fact]
    public void 沒有命中區段或沒有指令碼時不標()
    {
        Assert.Empty(SqlSearchDefinitionHighlight.Locate(Hit(SearchMatchTarget.Name, "Loan"), TableScript));
        Assert.Empty(SqlSearchDefinitionHighlight.Locate(Hit(SearchMatchTarget.Name, "Loan", new MatchSpan(0, 4)), ""));
    }

    /// <remarks>
    /// 取不到定義時整段是註解；那一份裡沒有 CREATE，命中也就沒有位置可以對——不標比標錯好。
    /// </remarks>
    [Fact]
    public void 整段註解的降級輸出仍對得上名稱()
    {
        const string script =
            "-- 無法為 [dbo].[Loan]（Table）產生可以執行的指令碼。\r\n" +
            "-- 查不到任何資料行。\r\n";
        var hit = Hit(SearchMatchTarget.Name, "Loan", new MatchSpan(0, 4));

        // 跳過檔頭之後整份都是註解，所以第二輪從頭再找一次，標在註解裡那個名稱上。
        var span = Assert.Single(SqlSearchDefinitionHighlight.Locate(hit, script));

        Assert.Equal("Loan", script.Substring(span.Start, span.Length));
    }

    private static SearchHit Hit(SearchMatchTarget target, string snippet, params MatchSpan[] spans) =>
        new("catalog", "catalog.table", target, "[dbo].[Loan]", "key", 100, null, snippet, spans);

    private static string LineAt(string text, int offset)
    {
        var start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var end = text.IndexOf('\n', offset);
        return text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
    }
}

public sealed class SqlSearchDefinitionCacheTests
{
    [Fact]
    public void 同一個鍵再要一次不必重查()
    {
        var cache = new SqlSearchDefinitionCache();
        cache.Add("Lib\u00011", "CREATE TABLE [dbo].[Loan];");

        Assert.True(cache.TryGet("Lib\u00011", out var script));
        Assert.Equal("CREATE TABLE [dbo].[Loan];", script);
    }

    [Fact]
    public void 沒記過的鍵回報找不到()
    {
        var cache = new SqlSearchDefinitionCache();

        Assert.False(cache.TryGet("Lib\u00011", out var script));
        Assert.Equal("", script);
    }

    [Fact]
    public void 超過筆數上限時丟掉最久沒用的那一份()
    {
        var cache = new SqlSearchDefinitionCache();

        foreach (var index in Enumerable.Range(0, SqlSearchDefinitionCache.MaximumEntries)) cache.Add(Key(index), "x");

        // 再用一次第一份，它就不是最久沒用的那一個了。
        Assert.True(cache.TryGet(Key(0), out _));
        cache.Add(Key(SqlSearchDefinitionCache.MaximumEntries), "x");

        Assert.Equal(SqlSearchDefinitionCache.MaximumEntries, cache.Count);
        Assert.True(cache.TryGet(Key(0), out _));
        Assert.False(cache.TryGet(Key(1), out _));
    }

    [Fact]
    public void 超過字元上限時也會丟()
    {
        var cache = new SqlSearchDefinitionCache();
        var half = new string('x', SqlSearchDefinitionCache.MaximumCharacters / 2 + 1);

        cache.Add(Key(0), half);
        cache.Add(Key(1), half);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(Key(1), out _));
    }

    /// <remarks>
    /// 收了之後會把其餘全部擠掉、自己也留不住，等於每選一次就把整份快取清空一次。
    /// </remarks>
    [Fact]
    public void 單獨一份就超過上限的不收()
    {
        var cache = new SqlSearchDefinitionCache();
        cache.Add(Key(0), "x");
        cache.Add(Key(1), new string('x', SqlSearchDefinitionCache.MaximumCharacters + 1));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(Key(0), out _));
    }

    [Fact]
    public void 清空之後一份都不留()
    {
        var cache = new SqlSearchDefinitionCache();
        cache.Add(Key(0), "x");
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(Key(0), out _));
    }

    private static string Key(int index) => "Lib\u0001" + index;
}
