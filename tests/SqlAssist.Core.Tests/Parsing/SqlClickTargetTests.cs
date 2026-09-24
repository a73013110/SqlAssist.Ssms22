using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// Ctrl＋點擊時，哪些字底下會出現底線。
/// </summary>
/// <remarks>
/// 錯在寬的那一側只是多一句「不是可辨識的資料庫物件」；錯在窄的那一側是欄位名稱點不動，
/// 所以只擋一定不是物件的兩種：保留字與內建名稱。
/// </remarks>
public sealed class SqlClickTargetTests
{
    private static SqlIdentifierReference? FindAtMarker(string textWithMarker, bool includeBuiltIns = true)
    {
        var input = SqlWithCaret.Parse(textWithMarker);
        return SqlClickTarget.FindAt(input.Text, input.Caret, includeBuiltIns);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 物件名稱兩種動作都能點(bool includeBuiltIns)
    {
        var reference = FindAtMarker("SELECT * FROM Lib|_Reader", includeBuiltIns);

        Assert.NotNull(reference);
        Assert.Equal("Lib_Reader", reference!.Name);
    }

    /// <summary>底線要蓋住整串限定名稱，點哪一段都是同一個物件。</summary>
    [Fact]
    public void 限定名稱整串都是連結()
    {
        var reference = FindAtMarker("SELECT * FROM dbo.Lib|_Reader");

        Assert.NotNull(reference);
        Assert.Equal(14, reference!.Start);
        Assert.Equal("dbo.Lib_Reader".Length, reference.Length);
    }

    [Theory]
    [InlineData("SEL|ECT * FROM Lib_Reader")]
    [InlineData("SELECT * FR|OM Lib_Reader")]
    [InlineData("SELECT * FROM Lib_Reader WH|ERE 1 = 1")]
    public void 保留字不是連結(string text)
    {
        Assert.Null(FindAtMarker(text));
    }

    /// <summary>加了括號的保留字就是合法的名稱。</summary>
    [Fact]
    public void 加括號的保留字是連結()
    {
        var reference = FindAtMarker("SELECT [Or|der] FROM Loan");

        Assert.NotNull(reference);
        Assert.Equal("Order", reference!.Name);
    }

    /// <summary>非保留的關鍵字常被拿來當欄位名稱，不能擋。</summary>
    [Fact]
    public void 非保留關鍵字仍是連結()
    {
        var reference = FindAtMarker("SELECT Ty|pe FROM Cat_BookCopy");

        Assert.NotNull(reference);
        Assert.Equal("Type", reference!.Name);
    }

    [Fact]
    public void 內建名稱只給看得懂它的動作()
    {
        Assert.NotNull(FindAtMarker("SELECT CON|VERT(varchar(10), GETDATE(), 112)", includeBuiltIns: true));
        Assert.Null(FindAtMarker("SELECT CON|VERT(varchar(10), GETDATE(), 112)", includeBuiltIns: false));
    }

    [Theory]
    [InlineData("SELECT 'Lib|_Reader'")]
    [InlineData("-- Lib|_Reader")]
    public void 字串與註解裡不是連結(string text)
    {
        Assert.Null(FindAtMarker(text));
    }
}
