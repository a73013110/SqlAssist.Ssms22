using System.Text;
using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>產物的格式：重跑時逐字相同，git diff 才看得出探測結果真的變了什麼。</summary>
public sealed class CatalogWriterTests
{
    [Fact]
    public void 清單每行不超過96欄()
    {
        var builder = new StringBuilder();
        var words = new string[30];

        for (var index = 0; index < words.Length; index++)
        {
            words[index] = "WORD" + index;
        }

        CatalogWriter.AppendWrapped(builder, "       ", words);
        var lines = builder.ToString().TrimEnd('\n').Split('\n');

        Assert.All(lines, line => Assert.True(line.Length <= 96, line));
        Assert.All(lines, line => Assert.StartsWith("        \"", line));
        Assert.Equal(30, builder.ToString().Split(',').Length - 1);
    }

    [Fact]
    public void 沒有字就不寫那一行()
    {
        var builder = new StringBuilder();

        CatalogWriter.AppendWrapped(builder, "           ", []);

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Fact]
    public void 字串跳脫反斜線與引號()
    {
        Assert.Equal("\"a\\\\b \\\"c\\\"\"", CatalogWriter.Quote("a\\b \"c\""));
    }

    [Fact]
    public void 沒有位置是None_其餘以直線連接()
    {
        Assert.Equal("SqlKeywordPosition.None", CatalogWriter.Flags([]));
        Assert.Equal("SqlKeywordPosition.SelectList | SqlKeywordPosition.DataSource", CatalogWriter.Flags(["SelectList", "DataSource"]));
    }

    [Fact]
    public void 產物只用LF()
    {
        var text = CatalogWriter.Write(new CatalogData { ScriptDomVersion = "1.0", ParserName = "TSql170Parser" });

        Assert.DoesNotContain("\r", text);
        Assert.Contains("internal const string SourceVersion = \"1.0\";", text);
    }
}
