using System.Linq;
using SqlAssist.Core.Keywords;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 「這個字當名字寫要不要加方括號」。
/// </summary>
/// <remarks>
/// 清單是產生的，因此這裡驗的不是有沒有列到某個字，而是這份清單與關鍵字清單
/// 確實是兩份東西：兩邊都有對方沒有的字，混用其中一份就會多加或漏加括號。
/// </remarks>
public sealed class SqlReservedIdentifierTests
{
    [Theory]
    [InlineData("ORDER")]
    [InlineData("KEY")]
    [InlineData("USER")]
    [InlineData("GROUP")]
    [InlineData("SELECT")]
    [InlineData("FROM")]
    [InlineData("TABLE")]
    [InlineData("CURRENT_USER")]
    public void 保留字必須加括號(string word)
    {
        Assert.True(SqlKeywordCatalog.IsReservedIdentifier(word));
    }

    [Theory]
    [InlineData("APPLY")]
    [InlineData("CATCH")]
    [InlineData("GO")]
    [InlineData("IDENTIFIER")]
    [InlineData("NEXT")]
    [InlineData("NOLOCK")]
    [InlineData("OFFSET")]
    [InlineData("OUTPUT")]
    [InlineData("PARTITION")]
    [InlineData("ROWS")]
    [InlineData("THROW")]
    [InlineData("TRY")]
    [InlineData("USING")]
    public void 非保留字的關鍵字不必加括號(string word)
    {
        // 目錄裡的 13 個非保留字。SELECT Output FROM t 是合法的 T-SQL，
        // 拿 IsKeyword 判斷括號就會把這些常見欄位名全部多包一層。
        Assert.True(SqlKeywordCatalog.IsKeyword(word));
        Assert.False(SqlKeywordCatalog.IsReservedIdentifier(word));
    }

    [Theory]
    [InlineData("IDENTITYCOL")]
    [InlineData("ROWGUIDCOL")]
    public void 不在關鍵字清單裡但仍要加括號(string word)
    {
        // 反過來的那一邊：詞法器把它們掃成識別字，所以進不了關鍵字清單，
        // 但剖析器不接受它們當名字。這兩個字正是「拿 IsKeyword 判斷」會漏掉的。
        Assert.False(SqlKeywordCatalog.IsKeyword(word));
        Assert.True(SqlKeywordCatalog.IsReservedIdentifier(word));
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("order")]
    [InlineData("oRdEr")]
    public void 大小寫不敏感(string word)
    {
        // 資料庫裡的欄位叫 Order 遠比叫 ORDER 常見。
        Assert.True(SqlKeywordCatalog.IsReservedIdentifier(word));
    }

    [Theory]
    [InlineData("Publishers")]
    [InlineData("LoanDetail")]
    [InlineData("Id")]
    [InlineData("Lib_Reader")]
    [InlineData("")]
    [InlineData(null)]
    public void 一般名稱不是保留字(string? word)
    {
        Assert.False(SqlKeywordCatalog.IsReservedIdentifier(word!));
    }

    [Fact]
    public void 內建資料型別不是保留字()
    {
        // int、xml 這類名稱當欄位名寫是合法的，不該被多加一層括號。
        Assert.False(SqlKeywordCatalog.IsReservedIdentifier("INT"));
        Assert.False(SqlKeywordCatalog.IsReservedIdentifier("XML"));
    }

    [Fact]
    public void 目錄裡大部分的關鍵字都是保留字()
    {
        // 上面兩份 Theory 列的字都是手挑的，這一條顧的是「產生器整段沒跑」——
        // 少了第三階段，這裡會是 0。
        var reserved = SqlKeywordCatalog.All
            .Count(keyword => SqlKeywordCatalog.IsReservedIdentifier(keyword));

        Assert.True(reserved > SqlKeywordCatalog.All.Count / 2);
    }
}
