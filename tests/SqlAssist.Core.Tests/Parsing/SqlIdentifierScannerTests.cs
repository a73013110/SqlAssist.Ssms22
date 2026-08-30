using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlIdentifierScannerTests
{
    private static SqlIdentifierReference? FindAtMarker(string textWithMarker)
    {
        var input = SqlWithCaret.Parse(textWithMarker);
        return SqlIdentifierScanner.FindAt(input.Text, input.Caret);
    }

    [Theory]
    [InlineData("SELECT * FROM Lib|_Reader")]
    [InlineData("SELECT * FROM |Lib_Reader")]
    [InlineData("SELECT * FROM Lib_Reader|")]
    public void 找出游標所在的識別字(string text)
    {
        var reference = FindAtMarker(text);

        Assert.NotNull(reference);
        Assert.Equal("Lib_Reader", reference!.Name);
        Assert.Null(reference.Qualifier);
    }

    [Fact]
    public void 回報識別字在原文中的位置()
    {
        var reference = FindAtMarker("SELECT * FROM Lib|_Reader");

        Assert.NotNull(reference);
        Assert.Equal(14, reference!.Start);
        Assert.Equal("Lib_Reader".Length, reference.Length);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Lib|_Reader", "dbo", "Lib_Reader")]
    [InlineData("SELECT * FROM [dbo].[Lib|_Reader]", "dbo", "Lib_Reader")]
    [InlineData("SELECT * FROM dbo.[Lib|_Reader]", "dbo", "Lib_Reader")]
    [InlineData("SELECT * FROM [dbo].Lib|_Reader", "dbo", "Lib_Reader")]
    public void 解析結構描述限定字(string text, string qualifier, string name)
    {
        var reference = FindAtMarker(text);

        Assert.NotNull(reference);
        Assert.Equal(name, reference!.Name);
        Assert.Equal(qualifier, reference.Qualifier);
    }

    [Fact]
    public void 限定形式的範圍涵蓋結構描述()
    {
        var reference = FindAtMarker("SELECT * FROM dbo.Lib|_Reader");

        Assert.NotNull(reference);
        Assert.Equal(14, reference!.Start);
        Assert.Equal("dbo.Lib_Reader".Length, reference.Length);
    }

    [Fact]
    public void 別名限定也會被解析為限定詞()
    {
        var reference = FindAtMarker("SELECT u.User|Name FROM Lib_Reader AS u");

        Assert.NotNull(reference);
        Assert.Equal("UserName", reference!.Name);
        Assert.Equal("u", reference.Qualifier);
    }

    [Fact]
    public void 方括號識別字內部可以解析()
    {
        var reference = FindAtMarker("SELECT * FROM [Loan De|tail]");

        Assert.NotNull(reference);
        Assert.Equal("Loan Detail", reference!.Name);
    }

    [Fact]
    public void 方括號內的跳脫右括號會還原()
    {
        var reference = FindAtMarker("SELECT * FROM [Weird]]Na|me]");

        Assert.NotNull(reference);
        Assert.Equal("Weird]Name", reference!.Name);
    }

    [Fact]
    public void 暫存表名稱可以解析()
    {
        var reference = FindAtMarker("SELECT * FROM #Tem|pLoans");

        Assert.NotNull(reference);
        Assert.Equal("#TempLoans", reference!.Name);
    }

    [Theory]
    [InlineData("SELECT 'Lib|_Reader'")]
    [InlineData("-- SELECT * FROM Lib|_Reader")]
    [InlineData("/* Lib|_Reader */")]
    public void 字串與註解內不解析(string text)
    {
        Assert.Null(FindAtMarker(text));
    }

    [Theory]
    [InlineData("SELECT * FROM Lib_Reader |")]
    [InlineData("SELECT *| FROM Lib_Reader")]
    public void 不在識別字上時回傳null(string text)
    {
        Assert.Null(FindAtMarker(text));
    }

    [Fact]
    public void 空字串回傳null()
    {
        Assert.Null(SqlIdentifierScanner.FindAt(string.Empty, 0));
    }

    [Fact]
    public void 位置超出範圍時回傳null()
    {
        Assert.Null(SqlIdentifierScanner.FindAt("SELECT", 99));
        Assert.Null(SqlIdentifierScanner.FindAt("SELECT", -1));
    }
}
