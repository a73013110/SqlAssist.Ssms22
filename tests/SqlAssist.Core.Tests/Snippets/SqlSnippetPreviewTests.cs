using System.Linq;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

/// <summary>
/// 片段在說明面板與浮動預覽裡顯示的，就是選下去之後實際插入的那一份。
/// </summary>
public sealed class SqlSnippetPreviewTests
{
    /// <summary>欄位填預設值、游標標記拿掉：預覽寫 <c>$table$</c> 的話，與插進去的文字對不上。</summary>
    [Fact]
    public void 顯示的是實際插入的文字()
    {
        var snippet = new SqlSnippet(
            "sf",
            "SELECT *\nFROM $table$$end$\n\n",
            placeholders: new[] { new SqlSnippetPlaceholder("table", "dbo.Lib_Reader") });

        Assert.Equal(new[] { "SELECT *", "FROM dbo.Lib_Reader" }, SqlSnippetPreview.Lines(snippet));
        Assert.Equal("SELECT *\nFROM dbo.Lib_Reader", SqlSnippetPreview.Text(snippet));
    }

    /// <summary>說明面板印得完的不開視窗；多一行就開，那一行只有視窗裝得下。</summary>
    [Fact]
    public void 印不完才值得開視窗()
    {
        var fits = new SqlSnippet("a", string.Join("\n", Enumerable.Repeat("SELECT 1;", SqlSnippetPreview.PanelLines)));
        var overflows = new SqlSnippet("b", string.Join("\n", Enumerable.Repeat("SELECT 1;", SqlSnippetPreview.PanelLines + 1)));

        Assert.False(SqlSnippetPreview.DeservesWindow(fits));
        Assert.True(SqlSnippetPreview.DeservesWindow(overflows));
    }

    /// <summary>中間的空白行是片段的段落，留著；只有結尾的空白行不算。</summary>
    [Fact]
    public void 中間的空白行留著()
    {
        var snippet = new SqlSnippet("be", "BEGIN\r\n\r\n    SELECT 1;\r\nEND\r\n");

        Assert.Equal(new[] { "BEGIN", string.Empty, "    SELECT 1;", "END" }, SqlSnippetPreview.Lines(snippet));
    }
}
