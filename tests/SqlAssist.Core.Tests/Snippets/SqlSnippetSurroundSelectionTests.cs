using System;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetSurroundSelectionTests
{
    [Theory]
    [InlineData("[|    SELECT 1;|]", "SELECT 1;", "SELECT 1;", "    ", false)]
    [InlineData("    [|SELECT 1;|]", "SELECT 1;", "SELECT 1;", "    ", false)]
    [InlineData("  [|  SELECT 1;|]", "SELECT 1;", "SELECT 1;", "    ", false)]
    [InlineData("SELECT [|  CopyNo + 1|] FROM dbo.Copy;", "  CopyNo + 1", "  CopyNo + 1", "", false)]
    [InlineData("    [|SELECT|] 1;", "SELECT", "SELECT", "    ", false)]
    [InlineData("[|    SELECT 1;\n|]SELECT 2;", "SELECT 1;", "SELECT 1;", "    ", false)]
    [InlineData("    SEL[|ECT 1;\n    SEL|]ECT 2;\nSELECT 3;", "SELECT 1;\n    SELECT 2;", "    SELECT 1;\n    SELECT 2;", "    ", true)]
    [InlineData("[|    SELECT 1;\n    SELECT 2;\n|]SELECT 3;", "SELECT 1;\n    SELECT 2;", "    SELECT 1;\n    SELECT 2;", "    ", false)]
    [InlineData("[|  \n    SELECT 1;|]", "  \n    SELECT 1;", "  \n    SELECT 1;", "", false)]
    public void 範圍與包入內容分開計算(string marked, string target, string text, string indent, bool expanded)
    {
        var (source, selection) = Resolve(marked);
        Assert.Equal(target, source.Substring(selection.Start, selection.Length));
        Assert.Equal(text, selection.Text);
        Assert.Equal(indent, selection.BaseIndent);
        Assert.Equal(expanded, selection.ExpandedToLines);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void 單行含行尾與跨行都保留外層縮排及下一行(string newLine)
    {
        foreach (var body in new[] { "    SELECT 1;", "    SELECT 1;" + newLine + "        SELECT 2;" })
        {
            var marked = "BEGIN" + newLine + "[|" + body + newLine + "|]END";
            var (source, selection) = Resolve(marked);
            var snippet = Block().WithSurroundText(selection.Text);
            var replacement = snippet.Expansion.GetText(newLine, selection.BaseIndent, out var caret);
            var actual = source.Substring(0, selection.Start) + replacement + source.Substring(selection.Start + selection.Length);
            var expectedBody = body.Replace(newLine, newLine + "    ");
            Assert.Equal("BEGIN" + newLine + "    BEGIN" + newLine + "    " + expectedBody +
                newLine + "    END" + newLine + "END", actual);
            Assert.Equal(replacement.Length, caret);
        }
    }

    [Fact]
    public void 重複包夾只增加樣板需要的那一層()
    {
        var (source, selection) = Resolve("[|    SELECT 1;|]");
        var first = source.Substring(0, selection.Start) + Block().WithSurroundText(selection.Text)
            .Expansion.GetText("\n", selection.BaseIndent, out _);
        var (_, second) = Resolve("[|" + first + "|]");
        var result = first.Substring(0, second.Start) + Block().WithSurroundText(second.Text)
            .Expansion.GetText("\n", second.BaseIndent, out _);
        Assert.Equal("    BEGIN\n        BEGIN\n            SELECT 1;\n        END\n    END", result);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 20)]
    [InlineData(3, int.MaxValue)]
    public void 無效範圍不做部分包夾(int start, int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlSnippetSurroundSelection.Resolve(new SqlStringText("SELECT 1;"), start, length));
    }

    private static (string Source, SqlSnippetSurroundSelection Selection) Resolve(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal) - 2;
        var source = marked.Replace("[|", "").Replace("|]", "");
        return (source, SqlSnippetSurroundSelection.Resolve(new SqlStringText(source), start, end - start));
    }

    private static SqlSnippet Block() => new("be", "BEGIN\n    $surround$\nEND$end$",
        placeholders: new[] { new SqlSnippetPlaceholder("surround", "SELECT 1;") });
}
