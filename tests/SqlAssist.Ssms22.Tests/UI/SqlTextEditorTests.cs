using System.Windows.Controls;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlTextEditorTests
{
    [Theory]
    [InlineData("SELECT 1;\r\nSELECT 2;\n\t")]
    [InlineData("SELECT N'圖書館';\0")]
    [InlineData("")]
    public void RawTextDoesNotPassThroughFormattedDocument(string sql)
    {
        WpfTest.Run(() =>
        {
            var editor = new SqlTextEditor(sql);
            var text = Assert.IsType<TextBox>(editor.Content);
            Assert.Equal(sql, editor.Text); Assert.False(editor.IsModified);
            text.Text += " "; Assert.True(editor.IsModified);
            text.Text = sql; Assert.False(editor.IsModified);
            Assert.Equal(sql, editor.Text);
            editor.IsReadOnly = true; Assert.True(text.IsReadOnly);
        });
    }
}
