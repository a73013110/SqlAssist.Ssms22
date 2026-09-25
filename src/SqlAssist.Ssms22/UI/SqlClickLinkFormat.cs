using System.ComponentModel.Composition;
using System.Windows;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.UI;

/// <summary>Ctrl＋點擊時滑鼠下那個名稱的底線。</summary>
/// <remarks>
/// 只加底線、不換顏色：字色沿用原本的分類，淺色、深色與高對比佈景都不必另外推導，
/// 底線的顏色也就跟著字色走。
/// </remarks>
internal static class SqlClickLinkFormat
{
    public const string Name = "SqlAssist.ClickLink";

    [Export(typeof(ClassificationTypeDefinition))]
    [Name(Name)]
    [BaseDefinition("text")]
    internal static ClassificationTypeDefinition Type = null!;
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = SqlClickLinkFormat.Name)]
[Name(SqlClickLinkFormat.Name)]
[Order(After = Priority.High)]
[UserVisible(false)]
internal sealed class SqlClickLinkFormatDefinition : ClassificationFormatDefinition
{
    public SqlClickLinkFormatDefinition()
    {
        DisplayName = ChromeText.ClickLinkFormat;
        TextDecorations = System.Windows.TextDecorations.Underline;
    }
}
