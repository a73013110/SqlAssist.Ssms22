using System;
using System.Data.SqlTypes;
using System.Linq;
using SqlAssist.Metadata.ResultGrid;
using Xunit;

namespace SqlAssist.Metadata.Tests.ResultGrid;

/// <summary>
/// 把選取範圍寫成 Markdown 表格。
/// </summary>
public sealed class SqlMarkdownTableScriptTests
{
    private static ResultGridTable Table((string Name, string Type)[] columns, object?[][] rows) =>
        new(columns.Select(c => new ResultGridColumn(c.Name, c.Type)).ToArray(), rows, isWholeResult: false);

    private static string[] Lines(string table) =>
        table.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void ColumnsAreAlignedAndSeparated()
    {
        var lines = Lines(SqlMarkdownTableScript.Build(Table(
            new[] { ("BranchId", "int"), ("CopyNo", "nvarchar(20)") },
            new[]
            {
                new object?[] { new SqlInt32(1), new SqlString("A01") },
                new object?[] { new SqlInt32(200), new SqlString("B") },
            })));

        Assert.Equal("| BranchId | CopyNo |", lines[0]);

        // 數值靠右對齊：一整欄數字靠右才對得起小數點。
        Assert.Equal("| -------: | ------ |", lines[1]);
        Assert.Equal("| 1        | A01    |", lines[2]);
        Assert.Equal("| 200      | B      |", lines[3]);
    }

    /// <remarks>
    /// 這一份的去處是工單或 PR 的說明欄，所以不加「由 SqlAssist 產生」那一行——
    /// 多一行就是使用者要刪的一行。另外兩個命令加，是因為它們的產出是 SQL。
    /// </remarks>
    [Fact]
    public void NoGeneratorComment()
    {
        var table = SqlMarkdownTableScript.Build(Table(
            new[] { ("CopyNo", "nvarchar(20)") },
            new[] { new object?[] { new SqlString("A01") } }));

        Assert.StartsWith("|", table);
        Assert.DoesNotContain("SqlAssist", table);
    }

    /// <remarks>
    /// 真正的 <c>NULL</c> 與一個內容剛好是 <c>NULL</c> 的字串，正是整組結果格線功能
    /// 一開始要解決的那一組，不該在最後一步又混回去。渲染出來一個是斜體一個不是。
    /// </remarks>
    [Fact]
    public void RealNullIsItalicAndLiteralNullTextIsNot()
    {
        var lines = Lines(SqlMarkdownTableScript.Build(Table(
            new[] { ("CopyNo", "nvarchar(20)") },
            new[]
            {
                new object?[] { null },
                new object?[] { new SqlString("NULL") },
            })));

        Assert.Equal("| *NULL* |", lines[2]);
        Assert.Equal("| NULL   |", lines[3]);
    }

    /// <remarks>
    /// 豎線切出一欄不存在的欄，換行把一列切成兩列——兩個字元都會拆掉表格。
    /// </remarks>
    [Fact]
    public void PipesAndNewlinesAreEscaped()
    {
        var lines = Lines(SqlMarkdownTableScript.Build(Table(
            new[] { ("Note", "nvarchar(max)") },
            new[] { new object?[] { new SqlString("a|b\r\nc") } })));

        Assert.Equal(3, lines.Length);
        Assert.Contains("a\\|b<br>c", lines[2]);
    }

    /// <remarks>
    /// 表格是給人讀的，所以字串不帶引號也不帶 <c>N</c> 前綴，日期也不帶引號。
    /// 但日期的精確度仍然跟著型別走，與儲存格視窗同一份判斷。
    /// </remarks>
    [Fact]
    public void ValuesAreShownWithoutSqlQuoting()
    {
        var lines = Lines(SqlMarkdownTableScript.Build(Table(
            new[] { ("CopyNo", "nvarchar(20)"), ("LoanedOn", "date") },
            new[] { new object?[] { new SqlString("O'Brien"), new DateTime(2024, 3, 4) } })));

        Assert.Contains("O'Brien", lines[2]);
        Assert.DoesNotContain("N'", lines[2]);
        Assert.Contains("2024-03-04", lines[2]);
        Assert.DoesNotContain("'2024", lines[2]);
    }

    [Fact]
    public void EmptySelectionExplainsWhatToDo()
    {
        Assert.StartsWith(
            "-- 無法從查詢結果產生 Markdown 表格。",
            SqlMarkdownTableScript.Build(Table(
                new[] { ("CopyNo", "nvarchar(20)") },
                Array.Empty<object?[]>())));
    }
}
