using System.Linq;
using SqlAssist.Core;
using SqlAssist.Core.Json;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests;

public sealed class SqlSnippetTests
{
    [Fact]
    public void 展開時把佔位符換成預設值並回報游標位置()
    {
        var snippet = new SqlSnippet(
            "ins",
            "INSERT INTO $table$ ($columns$)\nVALUES ($end$);",
            placeholders: new[]
            {
                new SqlSnippetPlaceholder("table", "dbo.T"),
                new SqlSnippetPlaceholder("columns", "a, b")
            });

        var text = snippet.Expand(out var caret);

        Assert.Equal("INSERT INTO dbo.T (a, b)\nVALUES ();", text);
        Assert.Equal(text.IndexOf("();", System.StringComparison.Ordinal) + 1, caret);
    }

    [Fact]
    public void 沒有游標標記時游標落在結尾()
    {
        var snippet = new SqlSnippet("ssf", "SELECT * FROM ");

        var text = snippet.Expand(out var caret);

        Assert.Equal("SELECT * FROM ", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void 沒有宣告的佔位符原樣保留()
    {
        // $$ 之間的東西不見得是佔位符，默默吃掉比多留幾個錢字號難查得多。
        var snippet = new SqlSnippet("x", "SELECT $unknown$");

        Assert.Equal("SELECT $unknown$", snippet.Expand(out _));
    }

    [Theory]
    [InlineData("SELECT * FROM $table$$end$", new[] { "table" })]
    [InlineData("$a$ $b$ $a$", new[] { "a", "b" })]
    [InlineData("SELECT 1", new string[0])]
    [InlineData("$end$", new string[0])]
    [InlineData("價格 $1,234$ 元", new string[0])]
    public void 從程式碼推導佔位符(string code, string[] expected)
    {
        Assert.Equal(expected, SqlSnippetPlaceholders.Extract(code).ToArray());
    }

    [Fact]
    public void 重算佔位符時保留已經設定好的預設值()
    {
        var existing = new[] { new SqlSnippetPlaceholder("table", "dbo.T", "資料表") };

        var reconciled = SqlSnippetPlaceholders.Reconcile("SELECT * FROM $table$ WHERE $id$ = 1", existing);

        Assert.Equal(new[] { "table", "id" }, reconciled.Select(item => item.Id).ToArray());
        Assert.Equal("dbo.T", reconciled[0].DefaultValue);
        Assert.Equal("資料表", reconciled[0].ToolTip);
        Assert.Equal(string.Empty, reconciled[1].DefaultValue);
    }

    [Fact]
    public void 寫出去再讀回來內容不變()
    {
        var original = SqlSnippetLibrary.CreateDefault().Set(new SqlSnippet(
            "ins",
            "INSERT INTO $table$\nVALUES ($end$);",
            "插入",
            "含「引號」與\t定位字元的說明",
            triggerFollowUp: true,
            new[] { new SqlSnippetPlaceholder("table", "dbo.T", "資料表名稱") }));

        var round = SqlSnippetSerializer.Deserialize(SqlSnippetSerializer.Serialize(original));

        Assert.Equal(original.Count, round.Count);
        Assert.True(round.TryGet("ins", out var snippet));
        Assert.Equal("INSERT INTO $table$\nVALUES ($end$);", snippet.Code);
        Assert.Equal("含「引號」與\t定位字元的說明", snippet.Description);
        Assert.True(snippet.TriggerFollowUp);
        Assert.Equal("dbo.T", snippet.Placeholders[0].DefaultValue);
    }

    [Fact]
    public void 讀取時略過沒有捷徑或沒有程式碼的項目()
    {
        // 檔案是使用者可以手改的，壞掉一筆不該讓其他 Snippet 一起消失。
        var library = SqlSnippetSerializer.Deserialize("""
            {
              "version": 1,
              "snippets": [
                { "shortcut": "ok", "code": "SELECT 1" },
                { "shortcut": "", "code": "SELECT 2" },
                { "shortcut": "empty", "code": "" },
                { "code": "SELECT 3" }
              ]
            }
            """);

        Assert.Equal(1, library.Count);
        Assert.True(library.TryGet("ok", out _));
    }

    [Fact]
    public void 內容不是_JSON_時丟出可定位的例外()
    {
        var exception = Assert.Throws<JsonParseException>(
            () => SqlSnippetSerializer.Deserialize("{ \"snippets\": [ }"));

        Assert.True(exception.Position > 0);
    }

    [Theory]
    [InlineData("ssf", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("my snippet", false)]
    [InlineData("sel-all", false)]
    [InlineData("sel_all2", true)]
    public void 捷徑必須是單一詞元且不能撞名(string shortcut, bool expected)
    {
        // 展開與比對都是在「一個詞元」上做的，含空白或標點的捷徑永遠打不出來。
        var library = SqlSnippetLibrary.CreateDefault();

        Assert.Equal(expected, library.ValidateShortcut(shortcut, allowedExisting: null, out _));
    }

    [Fact]
    public void 編輯既有項目時不會被自己的捷徑擋下來()
    {
        var library = SqlSnippetLibrary.CreateDefault();

        Assert.True(library.ValidateShortcut("ssf", allowedExisting: "ssf", out _));
    }

    [Fact]
    public void Snippet_進得了建議清單並帶著原始資料()
    {
        var library = SqlSnippetLibrary.CreateDefault();
        var suggestions = BuiltInSuggestionCatalog.Create(library);

        var ssf = suggestions.Single(item =>
            item.Kind == SuggestionKind.Snippet && item.DisplayText == "ssf");

        Assert.Equal("SELECT * FROM ", ssf.InsertionText);
        Assert.True(ssf.TriggerFollowUp);

        // 提交時要靠 Tag 拿回 $end$ 的位置。
        Assert.IsType<SqlSnippet>(ssf.Tag);
    }

    [Fact]
    public void 註解與尾隨逗號讀得進來()
    {
        // 使用者會直接用記事本改這個檔，這兩項寬容很划算。
        var library = SqlSnippetSerializer.Deserialize("""
            {
              // 我的片段
              "version": 1,
              "snippets": [
                { "shortcut": "a", "code": "SELECT 1" },
              ],
            }
            """);

        Assert.Equal(1, library.Count);
    }
}
