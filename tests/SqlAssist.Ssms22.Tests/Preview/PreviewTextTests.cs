using SqlAssist.Core.Localization;
using SqlAssist.Ssms22.Preview;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Preview;

/// <summary>預覽等內容的膠囊與停靠工具窗的標題跟著介面語言。</summary>
public sealed class PreviewTextTests
{
    [Fact]
    public void 英文介面的膠囊與工具窗標題()
    {
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            Assert.Equal("Loading dbo.Loan", PreviewText.CapsuleLoading("dbo.Loan"));
            Assert.Equal("Structure Preview - dbo.Loan", PreviewText.ToolWindowCaptionFor("dbo.Loan"));
        }
    }
}
