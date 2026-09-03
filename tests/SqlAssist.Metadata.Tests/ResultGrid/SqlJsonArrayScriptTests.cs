using System;
using System.Data.SqlTypes;
using System.Linq;
using SqlAssist.Metadata.ResultGrid;
using Xunit;

namespace SqlAssist.Metadata.Tests.ResultGrid;

/// <summary>
/// 把選取範圍寫成 JSON 陣列。
/// </summary>
public sealed class SqlJsonArrayScriptTests
{
    private static ResultGridTable Table((string Name, string Type)[] columns, params object?[][] rows) =>
        new(columns.Select(c => new ResultGridColumn(c.Name, c.Type)).ToArray(), rows, isWholeResult: false);

    [Fact]
    public void RowsBecomeObjectsKeyedByColumnName()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("BranchId", "int"), ("CopyNo", "nvarchar(20)") },
            new object?[] { new SqlInt32(1), new SqlString("A01") },
            new object?[] { new SqlInt32(2), new SqlString("A02") }));

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "[",
                "  {",
                "    \"BranchId\": 1,",
                "    \"CopyNo\": \"A01\"",
                "  },",
                "  {",
                "    \"BranchId\": 2,",
                "    \"CopyNo\": \"A02\"",
                "  }",
                "]"),
            json);
    }

    /// <remarks>
    /// 這是整組結果格線功能存在的理由，也是 JSON 唯一比 Markdown 表格強的地方：
    /// 真正的 <c>NULL</c> 是 <c>null</c>，一個內容剛好是 <c>NULL</c> 這四個字的
    /// 字串是 <c>"NULL"</c>，不必像 Markdown 那樣靠斜體區分。
    /// </remarks>
    [Fact]
    public void NullIsNotTheStringNull()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("Detail", "nvarchar(20)") },
            new object?[] { null },
            new object?[] { new SqlString("NULL") }));

        Assert.Contains("\"Detail\": null", json);
        Assert.Contains("\"Detail\": \"NULL\"", json);
    }

    /// <remarks>
    /// 數值不加引號——收下這份 JSON 的那一端才不必自己再轉一次。
    /// </remarks>
    [Fact]
    public void NumericTypesAreJsonNumbers()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("Fine", "decimal(18,2)"), ("Copies", "int") },
            new object?[] { new SqlDecimal(12.50m), new SqlInt32(-3) }));

        Assert.Contains("\"Fine\": 12.50", json);
        Assert.Contains("\"Copies\": -3", json);
    }

    /// <remarks>
    /// 判斷看的是伺服器給的型別，不是值長什麼樣：一整欄看起來都是整數、
    /// 實際上是 <c>varchar</c> 而其中一列有前導零時，寫成 JSON 數值就把值改掉了。
    /// </remarks>
    [Fact]
    public void TextThatLooksNumericStaysAString()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("PUBL_CODE", "varchar(10)") },
            new object?[] { new SqlString("007") }));

        Assert.Contains("\"PUBL_CODE\": \"007\"", json);
    }

    [Fact]
    public void BitBecomesTrueOrFalse()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("IsOnLoan", "bit") },
            new object?[] { new SqlBoolean(true) },
            new object?[] { new SqlBoolean(false) }));

        Assert.Contains("\"IsOnLoan\": true", json);
        Assert.Contains("\"IsOnLoan\": false", json);
    }

    /// <remarks>
    /// 日期走 <c>ResultGridCellText</c>，與 Markdown 表格同一份精確度判斷：
    /// 各算一次的話，同一個值在兩個命令裡會顯示不同的小數秒，看起來像資料有問題。
    /// </remarks>
    [Fact]
    public void DatesAreIsoStrings()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("LoanedOn", "datetime") },
            new object?[] { new SqlDateTime(new DateTime(2021, 12, 9, 18, 10, 43, 677)) }));

        Assert.Contains("\"LoanedOn\": \"2021-12-09T18:10:43.677\"", json);
    }

    /// <remarks>
    /// 引號、反斜線與換行不跳脫的話，整份 JSON 直接剖析失敗——比表格被拆掉一欄
    /// 嚴重，因為收下的那一端連一列都讀不到。
    /// </remarks>
    [Fact]
    public void QuotesBackslashesAndNewlinesAreEscaped()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("Detail", "nvarchar(50)") },
            new object?[] { new SqlString("說「\\」\r\n第二行\t結束") }));

        Assert.Contains("\"Detail\": \"說「\\\\」\\r\\n第二行\\t結束\"", json);
    }

    /// <remarks>
    /// 非 ASCII 照原樣寫出去，不轉成 <c>\uXXXX</c>：這份東西的去處是工單與設定檔，
    /// 中文值轉成六個字元一組之後就沒得讀了。
    /// </remarks>
    [Fact]
    public void NonAsciiIsLeftReadable()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("Detail", "nvarchar(50)") },
            new object?[] { new SqlString("開始搜尋") }));

        Assert.Contains("\"Detail\": \"開始搜尋\"", json);
    }

    /// <remarks>
    /// JSON 物件的鍵重複時，各家剖析器有的取第一個、有的取最後一個，
    /// 兩種都會安靜地少掉一欄，所以沿用產指令碼那一份去重。
    /// </remarks>
    [Fact]
    public void DuplicateAndMissingColumnNamesAreResolved()
    {
        var json = SqlJsonArrayScript.Build(Table(
            new[] { ("Id", "int"), ("id", "int"), ("", "int") },
            new object?[] { 1, 2, 3 }));

        Assert.Contains("\"Id\": 1", json);
        Assert.Contains("\"id_2\": 2", json);
        Assert.Contains("\"Column3\": 3", json);
    }

    [Fact]
    public void EmptySelectionExplainsItself()
    {
        var json = SqlJsonArrayScript.Build(
            new ResultGridTable(Array.Empty<ResultGridColumn>(), Array.Empty<object?[]>(), isWholeResult: true));

        Assert.StartsWith("-- 無法從查詢結果產生 JSON。", json);
    }
}
