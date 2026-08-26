using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SqlWildcardExpansionTextTests
{
    private static string Build(string[] columns, string indent, int width = 40)
    {
        return SqlWildcardExpansionText.Build(columns, indent, width, "\r\n");
    }

    [Fact]
    public void 放得下就排成一行()
    {
        Assert.Equal("Id, Name", Build(new[] { "Id", "Name" }, "SELECT "));
    }

    [Fact]
    public void 放不下就換行並對齊原本的位置()
    {
        var text = Build(
            new[] { "PublisherId", "PublisherName", "CreatedAt", "ModifiedAt" },
            "       ");

        Assert.Equal(
            "PublisherId, PublisherName,\r\n       CreatedAt, ModifiedAt",
            text);
    }

    /// <remarks>
    /// 第一個欄位接在 SELECT 後面，把它推到下一行只會讓 SELECT 孤零零地留在上一行。
    /// </remarks>
    [Fact]
    public void 第一個欄位永遠留在原地()
    {
        var text = Build(new[] { "AVeryLongColumnNameIndeed", "B" }, "          ", width: 20);

        Assert.StartsWith("AVeryLongColumnNameIndeed,", text);
    }

    [Fact]
    public void 沒有欄位時是空字串()
    {
        Assert.Equal(string.Empty, Build(new string[0], "SELECT "));
    }

    /// <remarks>
    /// 定位字元換成空白會讓對齊在定位寬度不是 4 的機器上跑掉，
    /// 把程式碼原樣抄過去則會在下一行留下一段看不出來的重複文字。
    /// </remarks>
    [Fact]
    public void 前導空白保留定位字元其餘換成空白()
    {
        Assert.Equal("\t\t       ", SqlWildcardExpansionText.BuildIndent("\t\tSELECT "));
    }
}
