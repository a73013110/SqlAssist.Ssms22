using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Linq;
using SqlAssist.Metadata.ResultGrid;
using Xunit;

namespace SqlAssist.Metadata.Tests.ResultGrid;

/// <summary>
/// 從選取範圍產生 <c>#temp</c> 指令碼與 <c>IN</c> 條件。
/// </summary>
public sealed class ResultGridScriptTests
{
    private static ResultGridTable Table(
        (string Name, string Type)[] columns,
        object?[][] rows,
        bool isWholeResult = false)
    {
        return new ResultGridTable(
            columns.Select(c => new ResultGridColumn(c.Name, c.Type)).ToArray(),
            rows,
            isWholeResult);
    }

    private static ResultGridTable CopyTable(params object?[][] rows) =>
        Table(new[] { ("BranchId", "int"), ("CopyNo", "nvarchar(20)") }, rows);

    [Fact]
    public void TempTableScriptIsRunnableEndToEnd()
    {
        var script = SqlTempTableScript.Build(CopyTable(
            new object?[] { new SqlInt32(1), new SqlString("A01") },
            new object?[] { new SqlInt32(2), null }));

        Assert.Contains("IF OBJECT_ID('tempdb..#SqlAssistRows') IS NOT NULL", script);
        Assert.Contains("DROP TABLE #SqlAssistRows;", script);
        Assert.Contains("CREATE TABLE #SqlAssistRows", script);
        Assert.Contains("    BranchId int NULL,", script);
        Assert.Contains("    CopyNo   nvarchar(20) NULL", script);
        Assert.Contains("INSERT INTO #SqlAssistRows (BranchId, CopyNo)", script);
        Assert.Contains("    (1, N'A01'),", script);
        Assert.Contains("    (2, NULL);", script);
        Assert.Contains("SELECT * FROM #SqlAssistRows;", script);
    }

    /// <remarks>
    /// 隨手查詢兩種常態，不處理的話 <c>CREATE TABLE</c> 直接是語法錯誤：
    /// 運算式欄位沒有名字，而 join 起來的兩張表常常各有一個同名欄位。
    /// 不分大小寫比對，因為資料庫的預設定序就是不分大小寫。
    /// </remarks>
    [Fact]
    public void ColumnNamesAreFilledInAndDeduplicated()
    {
        var table = Table(
            new[] { ("Id", "int"), ("id", "int"), ("", "int") },
            new[] { new object?[] { 1, 2, 3 } });

        Assert.Equal(new[] { "Id", "id_2", "Column3" }, table.ScriptColumnNames);
    }

    /// <remarks>
    /// <c>timestamp</c> 由引擎自己產生，明確插值會失敗——建得起來卻插不進去，
    /// 是這個功能最不該產出的那種指令碼。換成它實際的儲存形狀。
    /// </remarks>
    [Fact]
    public void RowVersionBecomesVarbinary()
    {
        var script = SqlTempTableScript.Build(Table(
            new[] { ("Version", "timestamp") },
            new[] { new object?[] { new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } } }));

        Assert.Contains("    Version varbinary(8) NULL", script);
        Assert.DoesNotContain("timestamp", script);
    }

    /// <remarks>
    /// T-SQL 的 <c>VALUES</c> 上限是 1000 列。超過的指令碼產得出來、貼得上去，
    /// 執行才失敗，所以要在這裡就切開。
    /// </remarks>
    [Fact]
    public void LongResultsAreSplitAcrossInserts()
    {
        var rows = new List<object?[]>();

        for (var index = 0; index < SqlTempTableScript.MaxRowsPerInsert + 1; index++)
        {
            rows.Add(new object?[] { index });
        }

        var script = SqlTempTableScript.Build(Table(new[] { ("Id", "int") }, rows.ToArray()));

        Assert.Equal(2, script.Split(new[] { "INSERT INTO" }, StringSplitOptions.None).Length - 1);
    }

    /// <remarks>
    /// 一欄轉不出來就整段拒絕。少那一欄的 <c>INSERT</c> 執行得動，
    /// 而使用者拿它 debug 時不會發現資料少了一塊。
    /// </remarks>
    [Fact]
    public void OneBadColumnRefusesTheWholeScript()
    {
        var script = SqlTempTableScript.Build(Table(
            new[] { ("BranchId", "int"), ("Shape", "geography") },
            new[] { new object?[] { 1, new Uri("https://example.invalid") } }));

        Assert.StartsWith("-- 無法從查詢結果產生暫存資料表指令碼。", script);
        Assert.Contains("Shape", script);
        Assert.DoesNotContain("CREATE TABLE #", script);
    }

    /// <remarks>
    /// 型別問不出來也一樣拒絕。猜一個型別的話 <c>CREATE TABLE</c> 產得出來，
    /// 但對不上原本的資料。
    /// </remarks>
    [Fact]
    public void MissingServerTypeRefusesTheWholeScript()
    {
        var script = SqlTempTableScript.Build(Table(
            new[] { ("Total", "") },
            new[] { new object?[] { 1 } }));

        Assert.StartsWith("-- 無法從查詢結果產生暫存資料表指令碼。", script);
        Assert.DoesNotContain("CREATE TABLE #", script);
    }

    [Fact]
    public void SingleColumnBecomesAnInList()
    {
        var predicate = SqlInPredicateScript.Build(Table(
            new[] { ("CopyNo", "nvarchar(20)") },
            new[]
            {
                new object?[] { new SqlString("A01") },
                new object?[] { new SqlString("A02") },
            }));

        Assert.Contains("CopyNo IN (N'A01', N'A02')", predicate);
    }

    /// <remarks>
    /// 整欄選下來常常只有幾個相異值。保留第一次出現的順序而不是排序，
    /// 那是使用者在格線上看到的順序。
    /// </remarks>
    [Fact]
    public void DuplicateRowsAreCollapsed()
    {
        var predicate = SqlInPredicateScript.Build(Table(
            new[] { ("CopyNo", "nvarchar(20)") },
            new[]
            {
                new object?[] { new SqlString("A02") },
                new object?[] { new SqlString("A01") },
                new object?[] { new SqlString("A02") },
            }));

        Assert.Contains("CopyNo IN (N'A02', N'A01')", predicate);
    }

    /// <remarks>
    /// <c>x IN (NULL)</c> 恆為 UNKNOWN：使用者明明選了那一列，條件卻永遠比不到
    /// 它，而且沒有錯誤訊息。這是這個功能最容易產生的「跑得動而答案是錯的」。
    /// </remarks>
    [Fact]
    public void NullInASingleColumnBecomesIsNull()
    {
        var predicate = SqlInPredicateScript.Build(Table(
            new[] { ("CopyNo", "nvarchar(20)") },
            new[]
            {
                new object?[] { new SqlString("A01") },
                new object?[] { null },
            }));

        Assert.Contains("(CopyNo IN (N'A01') OR CopyNo IS NULL)", predicate);
    }

    [Fact]
    public void AllNullsLeaveOnlyIsNull()
    {
        var predicate = SqlInPredicateScript.Build(Table(
            new[] { ("CopyNo", "nvarchar(20)") },
            new[] { new object?[] { null } }));

        Assert.Contains("CopyNo IS NULL", predicate);
        Assert.DoesNotContain(" IN (", predicate);
    }

    /// <remarks>
    /// SQL Server 沒有列值建構函式的 <c>IN</c>：
    /// <c>(BranchId, CopyNo) IN ((1, N'A01'))</c> 在別的資料庫上成立，
    /// 在 SQL Server 上是語法錯誤。
    /// </remarks>
    [Fact]
    public void CompositeKeyExpandsToOrConditions()
    {
        var predicate = SqlInPredicateScript.Build(CopyTable(
            new object?[] { new SqlInt32(1), new SqlString("A01") },
            new object?[] { new SqlInt32(2), null }));

        Assert.DoesNotContain("BranchId, CopyNo) IN", predicate);
        Assert.Contains("       (BranchId = 1 AND CopyNo = N'A01')", predicate);
        Assert.Contains("    OR (BranchId = 2 AND CopyNo IS NULL)", predicate);
    }

    /// <remarks>
    /// 外層一定包括號。接在既有的 <c>WHERE</c> 後面而少了括號的話，
    /// <c>AND</c> 的優先權比 <c>OR</c> 高會讓整句換一個意思——查得出結果，只是不對。
    /// </remarks>
    [Fact]
    public void CompositeKeyIsWrappedInParentheses()
    {
        var predicate = SqlInPredicateScript.Build(CopyTable(
            new object?[] { new SqlInt32(1), new SqlString("A01") }));

        var lines = predicate.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        Assert.Equal("(", lines[lines.Length - 3]);
        Assert.Equal(")", lines[lines.Length - 1]);
    }

    [Fact]
    public void EmptySelectionExplainsWhatToDo()
    {
        var table = Table(new[] { ("CopyNo", "nvarchar(20)") }, Array.Empty<object?[]>());

        Assert.StartsWith("-- 無法從查詢結果產生 IN 條件。", SqlInPredicateScript.Build(table));
        Assert.StartsWith("-- 無法從查詢結果產生暫存資料表指令碼。", SqlTempTableScript.Build(table));
    }

    /// <remarks>
    /// 開頭那一行要說清楚這塊資料的形狀。實測的結果有 178 欄，
    /// 「我剛剛到底選到了什麼」不是看一眼就知道的事。
    /// </remarks>
    [Fact]
    public void SourceCommentStatesTheShape()
    {
        var predicate = SqlInPredicateScript.Build(CopyTable(
            new object?[] { new SqlInt32(1), new SqlString("A01") }));

        Assert.StartsWith("-- 由 SqlAssist 從查詢結果產生：2 欄 × 1 列（選取範圍）。", predicate);
    }
}
