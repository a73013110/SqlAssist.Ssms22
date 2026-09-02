using System;
using System.Data.SqlTypes;
using SqlAssist.Metadata.ResultGrid;
using Xunit;

namespace SqlAssist.Metadata.Tests.ResultGrid;

/// <summary>
/// 一格的完整內容。
/// </summary>
public sealed class ResultGridCellTextTests
{
    private static ResultGridCellText Create(string type, object? value) =>
        ResultGridCellText.Create(new ResultGridColumn("CopyNo", type), value);

    /// <remarks>
    /// 文字給原文而不是字面值：看的人要讀的是內容本身，多一層引號跳脫只會擋路。
    /// </remarks>
    [Fact]
    public void TextIsShownAsIs()
    {
        var cell = Create("nvarchar(20)", new SqlString("O'Brien"));

        Assert.Equal("O'Brien", cell.Text);
        Assert.False(cell.IsNull);
        Assert.Contains("7 個字元", cell.Headline);
    }

    /// <remarks>
    /// 空字串與 <c>NULL</c> 在格線上都是不顯眼的一格，這個視窗要說得出是哪一種。
    /// </remarks>
    [Fact]
    public void NullAndEmptyTextAreDistinguishable()
    {
        var nothing = Create("nvarchar(20)", null);
        Assert.True(nothing.IsNull);
        Assert.Equal(string.Empty, nothing.Text);
        Assert.EndsWith("NULL", nothing.Headline);

        var empty = Create("nvarchar(20)", new SqlString(string.Empty));
        Assert.False(empty.IsNull);
        Assert.Equal(string.Empty, empty.Text);
        Assert.Contains("0 個字元", empty.Headline);
    }

    /// <remarks>
    /// 長度單位跟著型別走：文字算字元、二進位算位元組。混成同一個數字的話，
    /// 「這一欄會不會被截斷」就答不出來。
    /// </remarks>
    [Fact]
    public void BinaryIsCountedInBytesAndShownAsHex()
    {
        var cell = Create("varbinary(8)", new byte[] { 0x00, 0xFF, 0x10 });

        Assert.Equal("0x00FF10", cell.Text);
        Assert.Contains("3 個位元組", cell.Headline);
    }

    /// <remarks>
    /// 不換行的話，一段 8 KB 的 <c>varbinary</c> 是一條一萬六千字元的單行。
    /// </remarks>
    [Fact]
    public void LongBinaryWraps()
    {
        var cell = Create("varbinary(max)", new byte[100]);

        Assert.Contains("\n", cell.Text);
    }

    /// <remarks>
    /// 非文字非二進位的值給字面值：那時候看的人多半是要把它貼進一句 <c>WHERE</c>。
    /// </remarks>
    [Fact]
    public void OtherTypesAreShownAsLiterals()
    {
        Assert.Equal(
            "'2024-03-04'",
            Create("date", new DateTime(2024, 3, 4)).Text);

        Assert.Equal("42", Create("int", new SqlInt32(42)).Text);
    }

    /// <remarks>
    /// 標題要說得出這是哪一欄、什麼型別——這個視窗是從一片格線裡打開的，
    /// 少了這一句就分不出剛剛點的是哪一格。
    /// </remarks>
    [Fact]
    public void HeadlineNamesTheColumnAndType()
    {
        Assert.StartsWith("CopyNo（nvarchar(20)）· ", Create("nvarchar(20)", new SqlString("A01")).Headline);

        Assert.StartsWith(
            "（沒有資料行名稱）（?）· ",
            ResultGridCellText.Create(new ResultGridColumn(null, null), new SqlInt32(1)).Headline);
    }
}
