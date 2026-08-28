using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlIdentifierScannerTests
{
    /// <summary>
    /// 以 <c>|</c> 標記游標位置，讓測試資料一眼看得出停在哪裡。
    /// </summary>
    private static SqlIdentifierReference? FindAtMarker(string textWithMarker)
    {
        var position = textWithMarker.IndexOf('|');
        Assert.True(position >= 0, "測試字串必須含有代表游標位置的 | 符號。");
        return SqlIdentifierScanner.FindAt(textWithMarker.Remove(position, 1), position);
    }

    [Theory]
    [InlineData("SELECT * FROM Sys|_User")]
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
        var reference = FindAtMarker("SELECT * FROM Sys|_User");

        Assert.NotNull(reference);
        Assert.Equal(14, reference!.Start);
        Assert.Equal(8, reference.Length);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Sys|_User", "dbo", "Lib_Reader")]
    [InlineData("SELECT * FROM [dbo].[Sys|_User]", "dbo", "Lib_Reader")]
    [InlineData("SELECT * FROM dbo.[Sys|_User]", "dbo", "Lib_Reader")]
    [InlineData("SELECT * FROM [dbo].Sys|_User", "dbo", "Lib_Reader")]
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
        var reference = FindAtMarker("SELECT * FROM dbo.Sys|_User");

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
        var reference = FindAtMarker("SELECT * FROM [Order De|tail]");

        Assert.NotNull(reference);
        Assert.Equal("Order Detail", reference!.Name);
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
        var reference = FindAtMarker("SELECT * FROM #Tem|pOrders");

        Assert.NotNull(reference);
        Assert.Equal("#TempOrders", reference!.Name);
    }

    [Theory]
    [InlineData("SELECT 'Sys|_User'")]
    [InlineData("-- SELECT * FROM Sys|_User")]
    [InlineData("/* Sys|_User */")]
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
