using SqlAssist.Metadata.Formatting;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

/// <summary>
/// 擴充屬性說明的單行整理。
/// </summary>
/// <remarks>
/// 說明是使用者自己打進 <c>sp_addextendedproperty</c> 的字串，裡面什麼都有。
/// 滑鼠停留提示、建議清單的說明面板與結構預覽都只給它一行的位置——各自整理的
/// 症狀是同一段說明在三個地方換行方式不同，而其中一個會把資料格的列高撐破。
/// </remarks>
public sealed class SqlDescriptionTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void 沒有說明時回傳null(string? description)
    {
        Assert.Null(SqlDescriptionText.Collapse(description));
        Assert.Null(SqlDescriptionText.Summarize(description));
    }

    /// <remarks>
    /// 換行留著的話，資料格那一列會被撐成好幾行而超出固定的列高，
    /// 而標題底下那一行會把整個標題列頂高。
    /// </remarks>
    [Fact]
    public void 換行與連續空白收成單一空白()
    {
        Assert.Equal(
            "讀者編號 借閱系統的主鍵",
            SqlDescriptionText.Collapse("讀者編號\r\n\t借閱系統的主鍵  "));
    }

    [Fact]
    public void 收斂之後不改任何一個字()
    {
        Assert.Equal("Lib_Reader.Id", SqlDescriptionText.Collapse("  Lib_Reader.Id  "));
    }

    /// <remarks>
    /// 提示視窗不會自己斷行：長說明會排成一長行然後被螢幕邊界切掉，
    /// 而看的人看不出後面還有東西。省略號至少說得出「還有」。
    /// </remarks>
    [Fact]
    public void 超過上限時以省略號結尾()
    {
        var summary = SqlDescriptionText.Summarize(new string('借', 80));

        Assert.NotNull(summary);
        Assert.Equal(SqlDescriptionText.DefaultMaximumLength + 1, summary!.Length);
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void 沒有超過上限就不加省略號()
    {
        Assert.Equal("借閱紀錄", SqlDescriptionText.Summarize("借閱紀錄"));
    }

    /// <remarks>
    /// 結構預覽的資料格自己會省略並把全文留在 Tooltip 裡；先截一次等於把那份
    /// 全文也砍掉，所以 <see cref="SqlDescriptionText.Collapse"/> 不得偷偷截斷。
    /// </remarks>
    [Fact]
    public void 收斂不截斷()
    {
        var description = new string('借', 200);

        Assert.Equal(200, SqlDescriptionText.Collapse(description)!.Length);
    }
}
