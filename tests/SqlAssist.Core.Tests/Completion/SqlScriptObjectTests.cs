using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 指令碼自己宣告的物件：CTE、暫存資料表、資料表變數與暫存程序。
/// </summary>
/// <remarks>
/// 中繼資料只看得到目前連線資料庫的 <c>sys.objects</c>，這些名稱一個都不在裡面。
/// 症狀是使用者上一行才寫下的名稱，下一行打 <c>FROM </c> 或 <c>EXEC </c> 卻一個建議都沒有。
/// </remarks>
public sealed class SqlScriptObjectTests
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

    /// <summary>
    /// 逗號開啟的下一個資料來源同樣列得出指令碼自己宣告的名稱。
    /// </summary>
    /// <remarks>
    /// 只認 <c>FROM </c> 而不認逗號的症狀最難自己看出來：<c>FROM dbo.T a, </c>
    /// 之後打 <c>#</c> 什麼都沒有，而同一份指令碼上面幾行才剛
    /// <c>CREATE TABLE #table_temp</c>。
    /// </remarks>
    [Theory]
    [InlineData("CREATE TABLE #Tmp (a int);\r\nSELECT * FROM dbo.PUBLISHER p, |", "#Tmp")]
    [InlineData("CREATE TABLE #Tmp (a int);\r\nUPDATE a\r\nSET a.x = 1\r\nFROM dbo.T a, |", "#Tmp")]
    [InlineData(";WITH c1 AS (SELECT 1 AS a)\r\nSELECT * FROM dbo.PUBLISHER p, |", "c1")]
    public void 逗號之後也列出指令碼宣告的資料來源(string sqlWithCaret, string expected)
    {
        Assert.Equal(new[] { expected }, ScriptSources(sqlWithCaret));
    }

    /// <summary>
    /// 資料表變數與暫存資料表在這個位置是同一種東西。
    /// </summary>
    /// <remarks>
    /// 缺了這一份的症狀是 <c>DECLARE @rows TABLE (…)</c> 寫在上一行，
    /// <c>SELECT * FROM </c> 卻一個建議都沒有——非得自己先打一個小老鼠，
    /// 換到另一份清單去，而那份清單裡它與純量變數長得一模一樣。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @rows TABLE (CopyNo NVARCHAR(20));\r\nSELECT * FROM |", "@rows")]
    [InlineData(
        "CREATE FUNCTION f () RETURNS @out TABLE (a int) AS BEGIN\r\nSELECT * FROM |",
        "@out")]
    [InlineData("CREATE PROCEDURE p @rows dbo.LoanList READONLY AS\r\nSELECT * FROM |", "@rows")]
    public void FROM之後列出資料表變數(string sqlWithCaret, string expected)
    {
        Assert.Equal(new[] { expected }, ScriptSources(sqlWithCaret));
    }

    /// <summary>
    /// 看不出是資料表的小老鼠不算資料來源；資料表值參數有 <c>READONLY</c> 當憑據，算。
    /// </summary>
    /// <remarks>
    /// 井號開頭看形狀就分得完，小老鼠不行：<c>@readerId</c> 與 <c>@rows</c> 是同一種
    /// 詞元。一律放行的症狀是 <c>FROM </c> 之後列出使用者宣告過的每一個純量變數，
    /// 而它們一個都插不進那個位置。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @readerId INT;\r\nSELECT * FROM |")]
    [InlineData("DECLARE @rows dbo.LoanList;\r\nSELECT * FROM |")]
    [InlineData("DECLARE @Doc NVARCHAR(MAX);\r\nSELECT @Doc xml FROM |")]
    public void 不是資料表的變數不列出(string sqlWithCaret)
    {
        Assert.Empty(ScriptSources(sqlWithCaret));
    }

    /// <summary>
    /// xml 變數在資料來源的位置接 <c>.nodes(…)</c>，認的是宣告的型別；提交只補名字。
    /// </summary>
    [Theory]
    [InlineData("DECLARE @Doc xml = '<a/>'\r\nSELECT T.c.query('.') FROM |", "@Doc")]
    [InlineData("DECLARE @n INT, @Doc AS XML;\r\nSELECT * FROM |", "@Doc")]
    [InlineData("CREATE PROCEDURE p @Doc xml AS\r\nSELECT * FROM dbo.Copy JOIN |", "@Doc")]
    public void FROM之後列出xml變數(string sqlWithCaret, string expected)
    {
        Assert.Equal(new[] { expected }, ScriptSources(sqlWithCaret));
    }

    /// <summary>
    /// 提交之後的整句展開靠的是掛在項目上的那份宣告。
    /// </summary>
    /// <remarks>
    /// 少了它，<c>INSERT INTO @rows</c> 只補得到一個名稱——這種名稱中繼資料一列都
    /// 查不到，使用者還是得把每一個欄位自己打一遍。
    /// </remarks>
    [Fact]
    public void 資料表變數帶著自己的資料行清單()
    {
        var input = SqlWithCaret.Parse(
            "DECLARE @rows TABLE (CopyNo NVARCHAR(20), ReaderId INT);\r\nSELECT * FROM |");

        var suggestion = Assert.Single(
            SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret).ScriptSources);

        var table = Assert.IsType<SqlScriptTable>(suggestion.Tag);

        Assert.Equal(new[] { "CopyNo", "ReaderId" }, table.ColumnNames);
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

    /// <summary>
    /// 暫存程序接在 <c>EXEC</c> 之後，不接在 <c>FROM</c> 之後。
    /// </summary>
    /// <remarks>
    /// 井號名稱有兩種意思。全當成暫存資料表的症狀是 <c>EXEC #</c> 沒有清單，
    /// 而 <c>FROM </c> 之後卻列出一個選了就執行失敗的程序。
    /// </remarks>
    [Theory]
    [InlineData("CREATE PROCEDURE #Lib_Names AS SELECT 1;\nGO\nEXEC |", "#Lib_Names")]
    [InlineData("CREATE OR ALTER PROC ##Lib_Names AS SELECT 1;\nGO\nEXECUTE |", "##Lib_Names")]
    [InlineData("EXEC #Lib_Names;\nEXEC |", "#Lib_Names")]
    [InlineData("DECLARE @rc INT;\nEXEC @rc = #Lib_Names;\nEXEC |", "#Lib_Names")]
    [InlineData("DROP PROCEDURE #Lib_Names;\nCREATE TABLE #Loan (a int);\nEXEC |", "#Lib_Names")]
    public void EXEC之後列出暫存程序(string sqlWithCaret, string expected)
    {
        // 變數另有一份（EXEC @proc、EXEC @rc = p），見 SqlScriptVariableTests。
        Assert.Equal(new[] { expected }, ScriptSources(sqlWithCaret).Where(name => !name.StartsWith('@')));
    }

    [Theory]
    [InlineData("CREATE PROCEDURE #Lib_Names AS SELECT 1;\nGO\nSELECT * FROM |")]
    [InlineData("EXEC #Lib_Names;\nSELECT * FROM |")]
    public void 暫存程序不列在資料來源位置(string sqlWithCaret)
    {
        Assert.Empty(ScriptSources(sqlWithCaret));
    }

    /// <summary>
    /// 暫存程序與資料庫裡的程序同格：通過程序的目標過濾，插入時不補結構描述。
    /// </summary>
    [Fact]
    public void 暫存程序通過程序的目標過濾()
    {
        var input = SqlWithCaret.Parse("CREATE PROCEDURE #Lib_Names AS SELECT 1;\nGO\nEXEC #|");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        var matched = Assert.Single(SuggestionListProbe.Match(context.ScriptSources, context));

        Assert.Equal(SuggestionKind.Procedure, matched.Kind);
        Assert.Null(matched.SchemaName);
    }

    /// <summary>目標是資料來源時，CTE 與資料表同格通過過濾。</summary>
    [Fact]
    public void CTE通過資料來源的目標過濾()
    {
        var input = SqlWithCaret.Parse(";WITH CTE_TEST AS (SELECT 1 AS a)\r\nSELECT * FROM CTE|");
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        var matched = SuggestionListProbe
            .Match(context.ScriptSources, context)
            .Select(suggestion => suggestion.DisplayText);

        Assert.Equal(new[] { "CTE_TEST" }, matched);
    }
}
