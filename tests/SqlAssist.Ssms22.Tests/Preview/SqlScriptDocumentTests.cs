using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Tests.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Preview;

public sealed class SqlScriptDocumentTests
{
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

    private static ResourceDictionary CreateResources() => new()
    {
        [ScriptResource.FontFamily] = new FontFamily("Consolas"),
        [ScriptResource.FontSize] = 12.5,
        [ScriptResource.Background] = Brushes.White,
        [ScriptResource.Foreground] = Brushes.Black,
        [ScriptResource.Keyword] = Brushes.Blue,
        [ScriptResource.Comment] = Brushes.Green,
        [ScriptResource.String] = Brushes.Maroon,
        [ScriptResource.Number] = Brushes.Black
    };
}
