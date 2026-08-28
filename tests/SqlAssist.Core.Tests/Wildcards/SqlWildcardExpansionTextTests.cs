using SqlAssist.Core.Settings;
using SqlAssist.Core.Wildcards;
using Xunit;

namespace SqlAssist.Core.Tests.Wildcards;

public sealed class SqlWildcardExpansionTextTests
{
    /// <summary>放不下一行的四個欄位；用來分辨三種排法在「放不下」時的差異。</summary>
    private static readonly string[] TooWide =
    {
        "PublisherId", "PublisherName", "CreatedAt", "ModifiedAt"
    };

    private static string Build(
        string[] columns,
        string indent,
        SqlWildcardLayout layout = SqlWildcardLayout.OneLineWhenShort,
        int width = 40)
    {
        return SqlWildcardExpansionText.Build(columns, indent, layout, width, "\r\n");
    }

    [Fact]
    public void 放得下就排成一行()
    {
        Assert.Equal("Id, Name", Build(new[] { "Id", "Name" }, "SELECT "));
    }

    /// <remarks>放得下的時候，排滿行寬與「放得下就一行」是同一個結果。</remarks>
    [Fact]
    public void 排滿行寬時放得下也是一行()
    {
        Assert.Equal(
            "Id, Name",
            Build(new[] { "Id", "Name" }, "SELECT ", SqlWildcardLayout.FillWidth));
    }

    /// <remarks>唯一一個不看寬度的模式：兩個欄位也照樣拆成兩行。</remarks>
    [Fact]
    public void 每欄一行時放得下也照樣換行()
    {
        Assert.Equal(
            "Id,\r\n       Name",
            Build(new[] { "Id", "Name" }, "       ", SqlWildcardLayout.OnePerLine));
    }

    [Fact]
    public void 放不下時每欄一行並對齊原本的位置()
    {
        Assert.Equal(
            "PublisherId,\r\n       PublisherName,\r\n       CreatedAt,\r\n       ModifiedAt",
            Build(TooWide, "       "));
    }

    /// <remarks>同一份欄位，只有這個模式會排出「一行多個欄位」。</remarks>
    [Fact]
    public void 排滿行寬時放不下才換行()
    {
        Assert.Equal(
            "PublisherId, PublisherName,\r\n       CreatedAt, ModifiedAt",
            Build(TooWide, "       ", SqlWildcardLayout.FillWidth));
    }

    [Fact]
    public void 每欄一行與放得下就一行在放不下時結果相同()
    {
        Assert.Equal(
            Build(TooWide, "       ", SqlWildcardLayout.OnePerLine),
            Build(TooWide, "       ", SqlWildcardLayout.OneLineWhenShort));
    }

    /// <remarks>
    /// 第一個欄位接在 SELECT 後面，把它推到下一行只會讓 SELECT 孤零零地留在上一行。
    /// </remarks>
    [Theory]
    [InlineData(SqlWildcardLayout.OnePerLine)]
    [InlineData(SqlWildcardLayout.OneLineWhenShort)]
    [InlineData(SqlWildcardLayout.FillWidth)]
    public void 第一個欄位永遠留在原地(SqlWildcardLayout layout)
    {
        var text = Build(new[] { "AVeryLongColumnNameIndeed", "B" }, "          ", layout, width: 20);

        Assert.StartsWith("AVeryLongColumnNameIndeed,", text);
    }

    [Theory]
    [InlineData(SqlWildcardLayout.OnePerLine)]
    [InlineData(SqlWildcardLayout.OneLineWhenShort)]
    [InlineData(SqlWildcardLayout.FillWidth)]
    public void 沒有欄位時是空字串(SqlWildcardLayout layout)
    {
        Assert.Equal(string.Empty, Build(new string[0], "SELECT ", layout));
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
