using System.Linq;
using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 指令碼自己宣告的資料來源：CTE 與暫存資料表。
/// </summary>
/// <remarks>
/// 中繼資料只看得到目前連線資料庫的 <c>sys.objects</c>，這兩種名稱一個都不在裡面。
/// 症狀是使用者上一行才寫下的名稱，下一行打 <c>FROM </c> 卻一個建議都沒有。
/// </remarks>
public sealed class SqlScriptDataSourceTests
{
    private static string[] ScriptSources(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret)
            .ScriptSources
            .Select(suggestion => suggestion.DisplayText)
            .ToArray();
    }

    [Fact]
    public void FROM之後列出CTE()
    {
        Assert.Equal(
            new[] { "CTE_TEST" },
            ScriptSources(";WITH CTE_TEST AS (\r\n\tSELECT * FROM dbo.PUBLISHER\r\n)\r\nSELECT TOP (1) * FROM |"));
    }

    [Fact]
    public void 逗號分隔的多個CTE都列得出來()
    {
        Assert.Equal(
            new[] { "c1", "c2" },
            ScriptSources(";WITH c1 AS (SELECT 1 AS a), c2 AS (SELECT 2 AS b)\r\nSELECT * FROM |"));
    }

    /// <summary>
    /// 暫存資料表不分辨是哪一句建立的。
    /// </summary>
    /// <remarks>
    /// 井號開頭的識別字在 T-SQL 裡只有這一種意思，而 <c>CREATE TABLE</c>、
    /// <c>SELECT INTO</c>、<c>INSERT INTO</c> 各認一次的話，漏掉的那一種寫法
    /// 就會安靜地少一個名稱。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * INTO #Cust FROM dbo.PUBLISHER;\r\nSELECT * FROM |", "#Cust")]
    [InlineData("CREATE TABLE #Tmp (a int);\r\nSELECT * FROM |", "#Tmp")]
    [InlineData("CREATE TABLE ##Shared (a int);\r\nSELECT * FROM |", "##Shared")]
    public void FROM之後列出暫存資料表(string sqlWithCaret, string expected)
    {
        Assert.Equal(new[] { expected }, ScriptSources(sqlWithCaret));
    }

    [Fact]
    public void 同一個名稱只列一次()
    {
        Assert.Equal(
            new[] { "#Cust" },
            ScriptSources("SELECT * INTO #Cust FROM dbo.PUBLISHER;\r\nINSERT INTO #Cust\r\nSELECT * FROM |"));
    }

    /// <summary>
    /// CTE 與暫存資料表沒有結構描述，<c>FROM dbo.</c> 之後不該出現。
    /// </summary>
    /// <remarks>
    /// 同時也是效能上的分界：有限定字時連掃都不必掃。
    /// </remarks>
    [Fact]
    public void 限定字之後不列出()
    {
        Assert.Empty(ScriptSources(";WITH c AS (SELECT 1 AS a)\r\nSELECT * FROM dbo.|"));
    }

    /// <summary>
    /// 不在資料來源位置就不掃。
    /// </summary>
    /// <remarks>
    /// 這條路徑在每一次按鍵上，而只有 <c>FROM</c>、<c>JOIN</c> 之後用得到這一份。
    /// </remarks>
    [Theory]
    [InlineData(";WITH c AS (SELECT 1 AS a)\r\nSELECT c|")]
    [InlineData(";WITH c AS (SELECT 1 AS a)\r\nSELECT * FROM x WHERE c|")]
    public void 不是資料來源位置就不掃(string sqlWithCaret)
    {
        Assert.Empty(ScriptSources(sqlWithCaret));
    }

    /// <summary>目標是資料來源時，CTE 與資料表同格通過過濾。</summary>
    [Fact]
    public void CTE通過資料來源的目標過濾()
    {
        var input = SqlWithCaret.Parse(";WITH CTE_TEST AS (SELECT 1 AS a)\r\nSELECT * FROM CTE|");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        var matched = SuggestionMatcher
            .Match(context.ScriptSources, context)
            .Select(suggestion => suggestion.DisplayText);

        Assert.Equal(new[] { "CTE_TEST" }, matched);
    }
}
