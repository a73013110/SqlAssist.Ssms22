using System.Linq;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

/// <remarks>
/// 兩條規則原本只在管理介面存檔時跑，直接手改 <c>%APPDATA%\SqlAssist\snippets.json</c>
/// 的人完全繞得過去。載入時因此重跑同一份規則，但只回報不修資料：撞關鍵字的捷徑
/// 與重複的包夾錨點照樣留在清單裡，安靜地丟掉那一筆的話，使用者只會發現片段消失
/// 而沒有任何說明。
/// </remarks>
public sealed class SqlSnippetValidationTests
{
    [Fact]
    public void 沒有手改過的內建片段一筆都不會被標成不符規則()
    {
        var configuration = SqlSnippetMerger.Merge(SqlSnippetDefaults.Current, SqlSnippetDocument.Empty);

        Assert.All(configuration.Entries, entry => Assert.Null(entry.ValidationError));
    }

    [Fact]
    public void 手改進來的關鍵字捷徑被標出來但仍然留著()
    {
        var merged = Merge(new SqlSnippet("select", "SELECT $columns$$end$;", id: "user.keyword"));

        var entry = merged.Entries.Single(item => item.Snippet.Id == "user.keyword");
        Assert.Contains("關鍵字", entry.ValidationError);

        // 只回報不修資料：整份 JSON 壞掉才切唯讀，這一筆連建議清單都還在。
        Assert.True(merged.Library.TryGet("select", out var kept));
        Assert.Equal("user.keyword", kept.Id);
    }

    [Fact]
    public void 手改進來的重複包夾錨點被標出來()
    {
        var merged = Merge(new SqlSnippet(
            "twice",
            "BEGIN\n    $surround$\nEND\nELSE\nBEGIN\n    $surround$\nEND",
            id: "user.twice"));

        var entry = merged.Entries.Single(item => item.Snippet.Id == "user.twice");
        Assert.Contains("$surround$", entry.ValidationError);
    }

    /// <remarks>
    /// 被遮住的那一筆讓開捷徑那一條：撞名是計算結果不是錯誤，標成不符規則會讓
    /// 使用者連別的欄位都存不回去。同一筆的包夾錨點仍然要檢查——它與撞名無關。
    /// </remarks>
    [Fact]
    public void 被遮住的內建片段不因撞名被標成不符規則()
    {
        var merged = Merge(new SqlSnippet("ssf", "SELECT 1$end$;", id: "user.ssf"));

        var shadowed = merged.Entries.Single(item => item.Snippet.Id == "builtin.ssf");
        Assert.True(shadowed.IsShadowed);
        Assert.Null(shadowed.ValidationError);
    }

    /// <remarks>
    /// 管理介面存檔與載入標示走同一個進入點；分岔的症狀是清單上沒有標記的那一筆
    /// 在按下儲存時突然被退回。
    /// </remarks>
    [Theory]
    [InlineData("select", "SELECT 1;", false, false)]
    [InlineData("select", "SELECT 1;", true, true)]
    [InlineData("mine", "SELECT 1;", false, true)]
    [InlineData("mine", "BEGIN $surround$ $surround$ END", true, false)]
    [InlineData("", "SELECT 1;", false, false)]
    public void 存檔與載入共用同一份判斷(string shortcut, string code, bool shadowed, bool expected)
    {
        Assert.Equal(expected, SqlSnippetValidation.Validate(shortcut, code, shadowed, out var error));
        Assert.Equal(expected, error.Length == 0);
    }

    private static SqlSnippetConfiguration Merge(SqlSnippet custom)
    {
        return SqlSnippetMerger.Merge(
            SqlSnippetDefaults.Current,
            new SqlSnippetDocument(2, new[] { new SqlSnippetOverride(custom.Id, false, custom) }));
    }
}
