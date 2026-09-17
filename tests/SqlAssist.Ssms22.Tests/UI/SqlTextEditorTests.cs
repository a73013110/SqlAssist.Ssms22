using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlTextEditorTests
{
    private static readonly SolidColorBrush Keyword = Frozen(Colors.Red);
    private static readonly SolidColorBrush Plain = Frozen(Colors.Gray);

    [Theory]
    [InlineData("SELECT 1;\r\nSELECT 2;\n\t")]
    [InlineData("SELECT N'圖書館';\0")]
    [InlineData("")]
    public void RawTextDoesNotPassThroughFormattedDocument(string sql)
    {
        WpfTest.Run(() =>
        {
            var editor = new SqlTextEditor(sql);
            var text = Input(editor);
            Assert.Equal(sql, editor.Text); Assert.False(editor.IsModified);
            text.Text += " "; Assert.True(editor.IsModified);
            text.Text = sql; Assert.False(editor.IsModified);
            Assert.Equal(sql, editor.Text);
            editor.IsReadOnly = true; Assert.True(text.IsReadOnly);
        });
    }

    [Fact]
    public void VisibleSqlIsColoredOverATransparentInputAndLongSqlFallsBackToPlainText()
    {
        WpfTest.Run(() =>
        {
            var editor = new SqlTextEditor("SELECT CopyNo\n\tFROM Cat_BookCopy;");
            editor.Resources[ScriptResource.Keyword] = Keyword;
            editor.Resources[ScriptResource.Foreground] = Plain;
            Layout(editor);
            var input = Input(editor);
            // 輸入框只負責插入點與選取；文字交給著色層畫，否則兩層文字會疊出殘影。
            Assert.Same(Brushes.Transparent, input.Foreground);
            var brushes = Glyphs(editor).Select(glyph => glyph.ForegroundBrush).ToArray();
            Assert.Contains(Keyword, brushes);
            Assert.Contains(Plain, brushes);
            // Tab 之後的文字另外向 TextBox 要起點；「FROM」仍是關鍵字色。
            Assert.Equal(2, brushes.Count(brush => ReferenceEquals(brush, Keyword)));

            input.Text = new string('x', SqlScriptDocument.MaximumColorizedLength + 1);
            Layout(editor);
            Assert.NotSame(Brushes.Transparent, input.Foreground);
            Assert.Empty(Glyphs(editor));
        });
    }

    private static TextBox Input(SqlTextEditor editor) =>
        Assert.IsType<Grid>(editor.Content).Children.OfType<TextBox>().Single();

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(480, 240)); element.Arrange(new Rect(0, 0, 480, 240)); element.UpdateLayout();
    }

    private static IEnumerable<GlyphRunDrawing> Glyphs(SqlTextEditor editor)
    {
        var layer = Assert.IsType<Grid>(editor.Content).Children[1];
        return Flatten(VisualTreeHelper.GetDrawing(layer)).OfType<GlyphRunDrawing>();
    }

    private static IEnumerable<Drawing> Flatten(Drawing? drawing)
    {
        if (drawing is null) yield break;
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
            foreach (var nested in Flatten(child))
                yield return nested;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
