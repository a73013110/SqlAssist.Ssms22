using System.Linq;
using System.Threading.Tasks;
using SqlAssist.Core.Localization;
using Xunit;

namespace SqlAssist.TextGenerator.Tests;

/// <summary>
/// 產生器把各語言不一致的地方全部變成建置錯誤；這裡鎖住每一種錯誤都擋得下來，
/// 以及具名佔位符在譯文裡調換語序後，參數仍然對得上。
/// </summary>
public sealed class SqlTextGeneratorTests
{
    private const string Type = "SqlAssist.Sample.Feature.SampleText";

    [Fact]
    public void 沒有佔位符的產生屬性_有的產生方法且譯文可調換語序()
    {
        var run = GeneratorHarness.Generate(
            ("SampleText.zh-Hant.resjson", """{ "Title": "標題 {{x}}", "Range": "{from} 到 {to:N0}" }"""),
            ("SampleText.en.resjson", """{ "Title": "Title {{x}}", "Range": "up to {to:N0} from {from}" }"""));

        Assert.Empty(run.Diagnostics);
        Assert.Equal("標題 {x}", run.Invoke(Type, "Title"));
        Assert.Equal("甲 到 1,000", run.Invoke(Type, "Range", "甲", 1000));
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            Assert.Equal("Title {x}", run.Invoke(Type, "Title"));
            Assert.Equal("up to 1,000 from A", run.Invoke(Type, "Range", "A", 1000));
        }
    }

    [Theory]
    [InlineData("""{ "A": "一", "B": "二" }""", """{ "A": "one" }""", "SQLTXT008")]
    [InlineData("""{ "A": "一" }""", """{ "A": "one", "B": "two" }""", "SQLTXT009")]
    [InlineData("""{ "A": "{count} 筆" }""", """{ "A": "{total} rows" }""", "SQLTXT010")]
    [InlineData("""{ "A": "{0} 筆" }""", """{ "A": "{0} rows" }""", "SQLTXT007")]
    [InlineData("""{ "A": "少一個 {count" }""", """{ "A": "{count" }""", "SQLTXT007")]
    [InlineData("""{ "lower": "一" }""", """{ "lower": "one" }""", "SQLTXT006")]
    [InlineData("""{ "A": 1 }""", """{ "A": "one" }""", "SQLTXT005")]
    [InlineData("""{ "A": "一" """, """{ "A": "one" }""", "SQLTXT005")]
    [InlineData("""{ "A": "一" }""", """{ "A": "一" }""", "SQLTXT011")]
    [InlineData("""{ "A": "{count} 筆" }""", """{ "A": "{count}、rows" }""", "SQLTXT011")]
    public void 各語言不一致是建置錯誤(string source, string translated, string expected)
    {
        var run = GeneratorHarness.Generate(
            ("SampleText.zh-Hant.resjson", source),
            ("SampleText.en.resjson", translated));

        Assert.Contains(expected, run.Ids);
    }

    [Fact]
    public void 缺少一種語言或檔名不合規則都擋下來()
    {
        Assert.Contains("SQLTXT003", GeneratorHarness.Generate(("SampleText.zh-Hant.resjson", """{ "A": "一" }""")).Ids);
        Assert.Contains("SQLTXT002", GeneratorHarness.Generate(("SampleText.fr.resjson", """{ "A": "un" }""")).Ids);
        Assert.Contains("SQLTXT001", GeneratorHarness.Generate(new[] { ("SampleText.zh-Hant.resjson", """{ "A": "一" }""") }, string.Empty).Ids);
    }

    [Fact]
    public void 只驗證的檔案照樣擋下不一致_但不產生類別()
    {
        var files = new[]
        {
            ("SampleText.zh-Hant.resjson", """{ "A": "一", "B": "二" }"""),
            ("SampleText.en.resjson", """{ "A": "one" }"""),
        };

        var run = GeneratorHarness.Generate(files, "zh-Hant,en", resourceOnly: true);

        Assert.Contains("SQLTXT008", run.Ids);
        Assert.Null(run.Output.GetTypeByMetadataName(Type));
        Assert.NotNull(GeneratorHarness.Generate(files, "zh-Hant,en").Output.GetTypeByMetadataName(Type));
    }

    [Theory]
    [InlineData(@"D:\repo\src\SqlAssist.Ssms22\Search", "SqlAssist.Ssms22.Search")]
    [InlineData("/repo/src/SqlAssist.Core/Updates", "SqlAssist.Core.Updates")]
    [InlineData(@"D:\repo\tools\Other", null)]
    public void 命名空間由src底下的路徑推出(string directory, string? expected)
    {
        Assert.Equal(expected, SqlTextGenerator.NamespaceOf(directory));
    }

    [Fact]
    public async Task 字面中文要走文字檔_標上不在地化的出口與成員豁免()
    {
        const string source = """
            using System.ComponentModel;

            public static class Log
            {
                public static void Write([Localizable(false)] string message) { }

                public static void Show(string message) { }
            }

            public static class Sample
            {
                public static void Run(int count)
                {
                    Log.Show("給使用者看");
                    Log.Show($"共 {count} 筆");
                    Log.Write("只進紀錄");
                    Log.Write($"只進紀錄 {count}");
                    Log.Show("plain ascii");
                }

                [Localizable(false)]
                public static string Report() => "診斷報告";
            }
            """;

        var flagged = await GeneratorHarness.AnalyzeAsync(source);
        Assert.Equal(new[] { "SQLTXT100", "SQLTXT100" }, flagged.Select(diagnostic => diagnostic.Id));
        Assert.Empty(await GeneratorHarness.AnalyzeAsync(source, check: false));
    }
}
