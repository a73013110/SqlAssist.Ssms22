using System.Linq;
using System.Windows;
using SqlAssist.Core.Matching;
using SqlAssist.Core.SqlMemory;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Tests.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Preview;

public sealed class SqlScriptDocumentTests
{
    [Theory]
    [InlineData("SELECT 1;\nSELECT 2;\r\n\tSELECT 3;\n")]
    [InlineData("SELECT 1;\rSELECT 2;")]
    [InlineData("SELECT N'圖書館';\0 ")]
    [InlineData("")]
    public void CopyWholeSelectionPreservesExactOriginal(string sql)
    {
        WpfTest.Run(() =>
        {
            var viewer = new RichTextBox { Document = SqlScriptDocument.Build(sql, CreateResources()) };
            viewer.SelectAll();
            Assert.Equal(sql, SqlScriptDocument.ReadOriginalSelection(viewer, sql));
        });
    }

    [Fact]
    public void CopyAcrossRunsAndLineBreakPreservesOriginalNewline()
    {
        WpfTest.Run(() =>
        {
            const string sql = "SELECT 1;\nSELECT 2;";
            var viewer = new RichTextBox { Document = SqlScriptDocument.Build(sql, CreateResources()) };
            var paragraph = Assert.IsType<Paragraph>(viewer.Document.Blocks.FirstBlock);
            var runs = paragraph.Inlines.OfType<Run>().ToArray();
            viewer.Selection.Select(runs[0].ContentStart.GetPositionAtOffset(2), runs.Last().ContentEnd);
            Assert.Equal(sql.Substring(2), SqlScriptDocument.ReadOriginalSelection(viewer, sql));
        });
    }

    [Fact]
    public void UpdatingColorsPreservesDocumentRunsTextAndSelection()
    {
        WpfTest.Run(() =>
        {
            var resources = CreateResources();
            var document = SqlScriptDocument.Build("-- 註解\nSELECT 1, 'Loan', [Loan]", resources);
            var editor = new RichTextBox { Document = document };
            var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            var runs = paragraph.Inlines.OfType<Run>().ToArray();
            var keyword = Assert.Single(runs, run => run.Text == "SELECT");
            var quoted = Assert.Single(runs, run => run.Text == "[Loan]");
            editor.Selection.Select(keyword.ContentStart, keyword.ContentEnd);
            var original = new TextRange(document.ContentStart, document.ContentEnd).Text;

            foreach (var brush in new[] { Brushes.Blue, Brushes.LightBlue, Brushes.Blue })
            {
                resources[ScriptResource.Keyword] = brush;
                Assert.Equal(brush.Color, ThemeResourceSetTests.ColorOf(keyword.Foreground));
                Assert.Equal(Colors.Black, ThemeResourceSetTests.ColorOf(quoted.Foreground));
                Assert.Same(document, editor.Document);
                Assert.Equal(runs, paragraph.Inlines.OfType<Run>().ToArray());
                Assert.Equal(original, new TextRange(document.ContentStart, document.ContentEnd).Text);
                Assert.Equal("SELECT", editor.Selection.Text);
            }
        });
    }

    [Fact]
    public void FontChangesUpdateExistingDocument()
    {
        WpfTest.Run(() =>
        {
            var resources = CreateResources();
            var document = SqlScriptDocument.Build("SELECT 1", resources);
            resources[ScriptResource.FontSize] = 18.0;
            resources[ScriptResource.FontFamily] = new FontFamily("Cascadia Mono");
            Assert.Equal(18, document.FontSize);
            Assert.Equal("Cascadia Mono", document.FontFamily.Source);
        });
    }

    [Fact]
    public void LongScriptsRemainPlainTextAndStillFollowTheme()
    {
        WpfTest.Run(() =>
        {
            var resources = CreateResources();
            var script = new string('x', 60_001);
            var document = SqlScriptDocument.Build(script, resources);
            var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            var run = Assert.IsType<Run>(Assert.Single(paragraph.Inlines));
            resources[ScriptResource.Foreground] = Brushes.White;
            Assert.Equal(script, run.Text);
            Assert.Equal(Colors.White, ThemeResourceSetTests.ColorOf(run.Foreground));
        });
    }

    /// <summary>
    /// 每一處命中交出自己那幾個 Run，而且一處可能跨好幾個。
    /// </summary>
    /// <remarks>
    /// 高亮切的是原文位移，著色切的是詞法單元，兩條界線不會對齊。只交一個錨點的那一版，
    /// 換成「目前」的樣子時只有半個字會變色。
    /// </remarks>
    [Fact]
    public void 高亮交出每一處命中的那幾個Run()
    {
        WpfTest.Run(() =>
        {
            const string sql = "SELECT [Loan] FROM Loan;";
            SqlScriptDocument.Build(sql, CreateResources(),
                new[] { new MatchSpan(7, 6), new MatchSpan(19, 4) }, out var matches);

            Assert.Equal(2, matches.Count);
            // [Loan] 橫跨方括號與識別字兩個詞法單元，所以那一處不只一個 Run。
            Assert.Equal("[Loan]", string.Concat(matches[0].Select(run => run.Text)));
            Assert.Equal("Loan", string.Concat(matches[1].Select(run => run.Text)));
            Assert.All(matches, runs => Assert.All(runs, run => Assert.Equal(FontWeights.SemiBold, run.FontWeight)));
        });
    }

    /// <summary>
    /// 換成「目前」的樣子只改那一處，換回來時字色回到原本的著色分類。
    /// </summary>
    /// <remarks>
    /// 換的是資源鍵而不是筆刷：保存一次性筆刷的那一版在換佈景之後會留著上一個主題的顏色，
    /// 而文件不會重建。字色只在「目前」那一處蓋掉，一般命中仍要看得出語法著色。
    /// </remarks>
    [Fact]
    public void 目前那一處換色其餘不動而且換得回來()
    {
        WpfTest.Run(() =>
        {
            var resources = CreateResources();
            resources[ScriptResource.Highlight] = Brushes.LightYellow;
            resources[ScriptResource.HighlightCurrent] = Brushes.Orange;
            resources[ScriptResource.HighlightCurrentForeground] = Brushes.White;

            SqlScriptDocument.Build("SELECT Loan FROM Loan;", resources,
                new[] { new MatchSpan(7, 4), new MatchSpan(17, 4) }, out var matches);

            SqlScriptDocument.SetCurrentMatch(matches[1], current: true);

            Assert.Equal(Colors.Orange, ThemeResourceSetTests.ColorOf(matches[1][0].Background));
            Assert.Equal(Colors.White, ThemeResourceSetTests.ColorOf(matches[1][0].Foreground));
            Assert.Equal(Colors.LightYellow, ThemeResourceSetTests.ColorOf(matches[0][0].Background));

            SqlScriptDocument.SetCurrentMatch(matches[1], current: false);

            Assert.Equal(Colors.LightYellow, ThemeResourceSetTests.ColorOf(matches[1][0].Background));
            // 回到原本的著色分類，不是回到「某一種前景色」——這一段是識別字，走 Foreground。
            Assert.Equal(Colors.Black, ThemeResourceSetTests.ColorOf(matches[1][0].Foreground));
        });
    }

    /// <summary>
    /// SQL Memory 預覽的命中：清單那一輪的比對器算出的每一處都成一組 Run，導覽換「目前」只動那一處。
    /// </summary>
    /// <remarks>
    /// 預覽本身接著 SSMS 編輯器，進不了這個測試專案；這裡驗的是它交給文件的那一段，
    /// 也就是 <c>SqlMatchNavigation.Show</c> 走的同一條路（SQL Search 預覽也是）。
    /// </remarks>
    [Fact]
    public void SqlMemory預覽的命中每一處都成組而且導覽只換目前那一處()
    {
        WpfTest.Run(() =>
        {
            const string sql = "SELECT [CopyNo] FROM Cat_BookCopy WHERE CopyNo = @copyNo;";
            var model = new SqlMemoryBrowserModel
            {
                Search = "CopyNo",
                MatchOptions = TextMatchOptions.MatchCasing | TextMatchOptions.WholeWord
            };
            var spans = MatchHighlights.Locate(model.Query().Matcher, sql).Spans;

            SqlScriptDocument.Build(sql, CreateResources(), spans, out var matches);

            // 大小寫相同與整個字：Cat_BookCopy 與 @copyNo 都不標。
            Assert.Equal(2, matches.Count);
            Assert.All(matches, runs => Assert.Equal("CopyNo", string.Concat(runs.Select(run => run.Text))));

            var cursor = new MatchCursor(spans);
            SqlScriptDocument.SetCurrentMatch(matches[cursor.Index], current: true);
            Assert.True(cursor.MoveNext());
            SqlScriptDocument.SetCurrentMatch(matches[0], current: false);
            SqlScriptDocument.SetCurrentMatch(matches[cursor.Index], current: true);

            Assert.Equal(Colors.LightYellow, ThemeResourceSetTests.ColorOf(matches[0][0].Background));
            Assert.Equal(Colors.Orange, ThemeResourceSetTests.ColorOf(matches[1][0].Background));
            Assert.Equal(FontWeights.Bold, matches[1][0].FontWeight);
        });
    }

    [Fact]
    public void SqlMemory預覽沒有搜尋字時一處都不標()
    {
        WpfTest.Run(() =>
        {
            const string sql = "SELECT CopyNo FROM Cat_BookCopy;";
            var spans = MatchHighlights.Locate(new SqlMemoryBrowserModel().Query().Matcher, sql).Spans;

            var document = SqlScriptDocument.Build(sql, CreateResources(), spans, out var matches);

            Assert.Empty(matches);
            Assert.DoesNotContain(document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Run>()),
                run => run.FontWeight != FontWeights.Normal);
        });
    }

    private static ResourceDictionary CreateResources() => new()
    {
        [ScriptResource.FontFamily] = new FontFamily("Consolas"),
        [ScriptResource.FontSize] = 12.5,
        [ScriptResource.Background] = Brushes.White,
        [ScriptResource.Foreground] = Brushes.Black,
        [ScriptResource.Keyword] = Brushes.Blue,
        [ScriptResource.Comment] = Brushes.Green,
        [ScriptResource.String] = Brushes.Maroon,
        [ScriptResource.Number] = Brushes.Black,
        [ScriptResource.Highlight] = Brushes.LightYellow,
        [ScriptResource.HighlightCurrent] = Brushes.Orange,
        [ScriptResource.HighlightCurrentForeground] = Brushes.White
    };
}
