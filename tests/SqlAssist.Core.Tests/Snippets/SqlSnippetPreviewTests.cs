using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

/// <summary>
/// 片段在浮動預覽裡顯示的，就是選下去之後實際插入的那一份。
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

        Assert.Equal("SELECT *\nFROM dbo.Lib_Reader", SqlSnippetPreview.Text(snippet));
    }

    /// <summary>中間的空白行是片段的段落，留著；只有結尾的空白行不算。</summary>
    [Fact]
    public void 中間的空白行留著()
    {
        var snippet = new SqlSnippet("be", "BEGIN\r\n\r\n    SELECT 1;\r\nEND\r\n");

        Assert.Equal("BEGIN\n\n    SELECT 1;\nEND", SqlSnippetPreview.Text(snippet));
    }
}
