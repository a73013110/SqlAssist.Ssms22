using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetSurroundTests
{
    [Fact]
    public void 單行選取原樣接進錨點()
    {
        // 從一行中間選一段運算式時，前導空白是「選取起點在第幾欄」而不是縮排；
        // 去掉它會把內容往左推。
        Assert.Equal("  CopyNo + 1", SqlSnippetSurround.Reindent("  CopyNo + 1", "    "));
    }

    [Fact]
    public void 多行選取去掉共同縮排再對齊到錨點()
    {
        var text = SqlSnippetSurround.Reindent(
            "        SELECT CopyNo\n        FROM dbo.Loan\n        WHERE CopyNo > 0;",
            "    ");

        // 第一行由樣板自己的錨點縮排帶著，所以這裡不重複補。
        Assert.Equal(
            "SELECT CopyNo\n    FROM dbo.Loan\n    WHERE CopyNo > 0;",
            text);
    }

    [Fact]
    public void 巢狀的相對縮排保留下來()
    {
        var text = SqlSnippetSurround.Reindent(
            "    IF @@ROWCOUNT = 0\n        THROW 50000, 'none', 1;",
            "  ");

        Assert.Equal("IF @@ROWCOUNT = 0\n      THROW 50000, 'none', 1;", text);
    }

    [Fact]
    public void 空白行不補縮排也不留尾隨空白()
    {
        var text = SqlSnippetSurround.Reindent(
            "    SELECT 1;\n   \n    SELECT 2;",
            "\t");

        Assert.Equal("SELECT 1;\n\n\tSELECT 2;", text);
    }

    /// <remarks>
    /// 混用 Tab 與空白時只共到相同的那一段。比欄數要有一個 Tab 寬度的設定值，
    /// 猜錯的症狀是包夾之後整段偏移，而且沒有任何錯誤。
    /// </remarks>
    [Fact]
    public void 混用定位字元與空白時只去掉真正共同的前綴()
    {
        var text = SqlSnippetSurround.Reindent("\t  SELECT 1;\n\tSELECT 2;", "");

        Assert.Equal("  SELECT 1;\nSELECT 2;", text);
    }

    [Fact]
    public void 換行一律正規化成單一格式()
    {
        Assert.Equal("SELECT 1;\nSELECT 2;", SqlSnippetSurround.Reindent("SELECT 1;\r\nSELECT 2;", ""));
        Assert.Equal("SELECT 1;\nSELECT 2;", SqlSnippetSurround.Reindent("SELECT 1;\rSELECT 2;", ""));
    }

    [Fact]
    public void 沒有選取內容時是空字串()
    {
        Assert.Equal(string.Empty, SqlSnippetSurround.Reindent(null, "    "));
        Assert.Equal(string.Empty, SqlSnippetSurround.Reindent(string.Empty, "    "));
    }
}
