using SqlAssist.Core.Statements;
using Xunit;

namespace SqlAssist.Core.Tests.Statements;

public sealed class SqlLiteralDefaultsTests
{
    [Theory]
    [InlineData("nvarchar(100)", "N''")]
    [InlineData("nchar(2)", "N''")]
    [InlineData("sysname", "N''")]
    [InlineData("varchar(10)", "''")]
    [InlineData("text", "''")]
    [InlineData("int", "0")]
    [InlineData("bit", "0")]
    [InlineData("decimal(18,2)", "0")]
    [InlineData("money", "0")]
    [InlineData("varbinary(max)", "0x")]
    [InlineData("uniqueidentifier", "NEWID()")]
    public void 依型別給預留值(string dataType, string expected)
    {
        Assert.Equal(expected, SqlLiteralDefaults.ForType(dataType));
    }

    /// <remarks>
    /// 空字串轉成日期是 1900-01-01——一個執行得動的錯值。NULL 在 NOT NULL 的欄位上
    /// 會失敗，而失敗看得見；預留值要的正是「看得出來還沒填」。
    /// </remarks>
    [Theory]
    [InlineData("date")]
    [InlineData("datetime")]
    [InlineData("datetime2(7)")]
    [InlineData("datetimeoffset(7)")]
    public void 日期型別不給空字串(string dataType)
    {
        Assert.Equal("NULL", SqlLiteralDefaults.ForType(dataType));
    }

    /// <remarks>沒有共通字面值寫法的型別一律 NULL，不猜。</remarks>
    [Theory]
    [InlineData("hierarchyid")]
    [InlineData("geography")]
    [InlineData("sql_variant")]
    [InlineData("dbo.LibraryCardType")]
    [InlineData("")]
    [InlineData(null)]
    public void 認不得的型別給NULL(string? dataType)
    {
        Assert.Equal("NULL", SqlLiteralDefaults.ForType(dataType));
    }

    [Fact]
    public void 大小寫與空白不影響判斷()
    {
        Assert.Equal("N''", SqlLiteralDefaults.ForType(" NVarChar(50) "));
    }
}
