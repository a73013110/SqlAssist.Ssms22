using System;
using System.Data.SqlTypes;
using System.Globalization;
using System.Threading;
using SqlAssist.Metadata.ResultGrid;
using Xunit;

namespace SqlAssist.Metadata.Tests.ResultGrid;

/// <summary>
/// 儲存格的值轉成 T-SQL 字面值。
/// </summary>
public sealed class SqlValueLiteralTests
{
    private static string Format(object? value, string? type = null)
    {
        Assert.True(SqlValueLiteral.TryFormat(value, type, out var literal, out var reason), reason);
        return literal;
    }

    /// <remarks>
    /// 三種 NULL 的形狀都要認得。<c>SqlString.Null</c> 是最容易漏的一種——
    /// 它不是 <c>null</c> 參考，型別與有值的 <c>SqlString</c> 完全相同，
    /// 只有 <c>IsNull</c> 分得出來。漏掉它的下場是產出 <c>N''</c>，
    /// 而空字串與 <c>NULL</c> 在查詢裡是兩回事。
    /// </remarks>
    [Fact]
    public void NullShapesAllBecomeNull()
    {
        Assert.Equal("NULL", Format(null));
        Assert.Equal("NULL", Format(DBNull.Value));
        Assert.Equal("NULL", Format(SqlString.Null));
        Assert.Equal("NULL", Format(SqlInt32.Null));
        Assert.Equal("NULL", Format(SqlDateTime.Null));
    }

    /// <remarks>
    /// 空字串必須與 NULL 分得開。這一組正是「不走剪貼簿」的理由之一：
    /// 兩者在 TSV 裡都是空欄位。
    /// </remarks>
    [Fact]
    public void EmptyStringIsNotNull()
    {
        Assert.Equal("N''", Format(new SqlString(string.Empty)));
    }

    [Fact]
    public void SingleQuotesAreDoubled()
    {
        Assert.Equal("N'O''Brien'", Format(new SqlString("O'Brien")));
    }

    /// <remarks>
    /// 型別說得出來是 <c>varchar</c> 才省掉 <c>N</c>；問不出型別時一律加上。
    /// 反過來做的話，非拉丁字元會被換成問號而且沒有任何錯誤訊息。
    /// </remarks>
    [Theory]
    [InlineData("varchar(20)", "'A01'")]
    [InlineData("char(3)", "'A01'")]
    [InlineData("nvarchar(20)", "N'A01'")]
    [InlineData("", "N'A01'")]
    [InlineData(null, "N'A01'")]
    public void UnicodePrefixFollowsServerType(string? serverType, string expected)
    {
        Assert.Equal(expected, Format(new SqlString("A01"), serverType));
    }

    /// <remarks>
    /// 日期一律 ISO 8601。<c>'2024-03-04'</c> 這種寫法插進 <c>datetime</c> 會
    /// 隨連線的 <c>DATEFORMAT</c> 改變解讀，同一段指令碼換一個人執行就變成另一天。
    /// </remarks>
    [Theory]
    [InlineData("date", "'2024-03-04'")]
    [InlineData("datetime", "'2024-03-04T10:30:05.123'")]
    [InlineData("smalldatetime", "'2024-03-04T10:30:05.123'")]
    [InlineData("datetime2(7)", "'2024-03-04T10:30:05.1230000'")]
    [InlineData(null, "'2024-03-04T10:30:05.1230000'")]
    public void DateTimePrecisionFollowsServerType(string? serverType, string expected)
    {
        var moment = new DateTime(2024, 3, 4, 10, 30, 5, 123, DateTimeKind.Unspecified);
        Assert.Equal(expected, Format(moment, serverType));
    }

    /// <remarks>
    /// 實測的結果裡 <c>date</c> 給的是原生 <see cref="DateTime"/>，而
    /// <c>datetime</c> 給的是 <see cref="SqlDateTime"/>。兩族都要走同一條路，
    /// 否則其中一種會掉到「認不得的型別」而讓整段指令碼被拒絕。
    /// </remarks>
    [Fact]
    public void SqlDateTimeAndDateTimeAgree()
    {
        var moment = new DateTime(2024, 3, 4, 10, 30, 5, 123);
        Assert.Equal(Format(moment, "datetime"), Format(new SqlDateTime(moment), "datetime"));
    }

    [Fact]
    public void BooleansBecomeBits()
    {
        Assert.Equal("1", Format(SqlBoolean.True, "bit"));
        Assert.Equal("0", Format(false, "bit"));
    }

    [Fact]
    public void BinaryBecomesHexLiteral()
    {
        Assert.Equal("0x00FF10", Format(new byte[] { 0x00, 0xFF, 0x10 }, "varbinary(8)"));
    }

    /// <remarks>
    /// 長度為零的 <c>varbinary</c> 是合法的值，但 <c>0x</c> 後面什麼都沒有
    /// 不是合法的字面值——貼上去是語法錯誤。
    /// </remarks>
    [Fact]
    public void EmptyBinaryStillProducesALiteral()
    {
        Assert.Equal("0x00", Format(Array.Empty<byte>(), "varbinary(8)"));
    }

    /// <remarks>
    /// 精確度超過 <see cref="decimal"/> 的 <c>SqlDecimal</c> 不能先轉成
    /// <c>decimal</c>——那一步會溢位。
    /// </remarks>
    [Fact]
    public void HighPrecisionDecimalSurvives()
    {
        var value = SqlDecimal.Parse("123456789012345678901234567890.12345678");
        Assert.Equal("123456789012345678901234567890.12345678", Format(value, "decimal(38,8)"));
    }

    /// <remarks>
    /// 小數點在部分地區設定裡是逗號，而 <c>1,5</c> 寫進 <c>VALUES</c> 會被當成
    /// 兩個值。每個呼叫端都指定了不變文化，這裡確認換一個執行緒文化也不動搖。
    /// </remarks>
    [Fact]
    public void NumbersIgnoreCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", Format(1.5m, "decimal(18,2)"));
            // money 的小數位固定是四位，"1.5000" 才是它真正的值。
            Assert.Equal("1.5000", Format(new SqlMoney(1.5m), "money"));
            Assert.Equal("1.5", Format(1.5d, "float"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    /// <remarks>
    /// 認不得的型別要回報失敗，不能回退成字串。空間型別的 <c>ToString()</c>
    /// 給得出 WKT，包成 <c>N'...'</c> 也插得進去，但那已經不是原本的值了。
    /// </remarks>
    [Fact]
    public void UnknownTypeIsRefusedWithAReason()
    {
        Assert.False(
            SqlValueLiteral.TryFormat(new Uri("https://example.invalid"), "geography", out var literal, out var reason));
        Assert.Equal(string.Empty, literal);
        Assert.Contains("Uri", reason);
    }
}
