using System.Linq;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetExpansionTests
{
    [Fact]
    public void 不可變Snippet會快取同一份展開計算()
    {
        var snippet = new SqlSnippet("x", "SELECT $value$$end$", placeholders: new[]
        {
            new SqlSnippetPlaceholder("value", "1")
        });

        Assert.Same(snippet.Expansion, snippet.Expansion);
    }

    [Fact]
    public void 一次計算純文字游標與同名欄位位置()
    {
        var snippet = new SqlSnippet(
            "x",
            "$name$ + $name$$end$",
            placeholders: new[] { new SqlSnippetPlaceholder("name", "CopyNo") });

        var expansion = SqlSnippetExpansion.Create(snippet);

        Assert.Equal("CopyNo + CopyNo", expansion.Text);
        Assert.Equal(expansion.Text.Length, expansion.CaretOffset);
        var field = Assert.Single(expansion.Fields);
        Assert.Equal(new[] { 0, 9 }, field.Occurrences.Select(item => item.Start).ToArray());
        Assert.Equal("$name$ + $name$$end$", expansion.NativeCode);
    }

    [Fact]
    public void 未宣告標記與一般錢字號對原生引擎跳脫但純文字原樣保留()
    {
        var snippet = new SqlSnippet(
            "x",
            "PRINT '$5'; SELECT $known$, $unknown$, $$end$",
            placeholders: new[] { new SqlSnippetPlaceholder("known", "1") });

        var expansion = SqlSnippetExpansion.Create(snippet);

        Assert.Equal("PRINT '$5'; SELECT 1, $unknown$, $", expansion.Text);
        Assert.Equal("PRINT '$$5'; SELECT $known$, $$unknown$$, $$$end$", expansion.NativeCode);
    }

    [Fact]
    public void selected是保留標記而不是可編輯欄位()
    {
        var snippet = new SqlSnippet("x", "SELECT $selected$$end$");
        var expansion = SqlSnippetExpansion.Create(snippet);

        Assert.Equal("SELECT ", expansion.Text);
        Assert.Empty(expansion.Fields);
        Assert.Equal("SELECT $selected$$end$", expansion.NativeCode);
    }

    [Fact]
    public void 依編輯器換行格式產生文字且同步修正游標位移()
    {
        var snippet = new SqlSnippet("x", "BEGIN\n    SELECT 1;$end$\nEND");
        var expansion = SqlSnippetExpansion.Create(snippet);

        var text = expansion.GetText("\r\n", out var caret);

        Assert.Equal("BEGIN\r\n    SELECT 1;\r\nEND", text);
        Assert.Equal(text.IndexOf("\r\nEND", System.StringComparison.Ordinal), caret);
        Assert.Contains("SELECT 1;$end$\r\nEND", expansion.GetNativeCode("\r\n"));
    }
}
