using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlAssistChrome
{
    public static ThemeBrush BlockBrush(BlockKind kind) => kind switch
    {
        BlockKind.Try => ThemeBrush.BlockTry, BlockKind.Catch => ThemeBrush.BlockCatch,
        BlockKind.Case => ThemeBrush.BlockCase, BlockKind.Parenthesis => ThemeBrush.BlockParenthesis,
        BlockKind.Bracket => ThemeBrush.BlockBracket, BlockKind.String => ThemeBrush.BlockParenthesis, _ => ThemeBrush.Block
    };

    public static Border CreateBlockGlyph(BlockKind kind, double height, string description)
    {
        var glyph = new Border { Width = 3, Height = Math.Max(1, height), ToolTip = description }
            .WithTheme(Border.BackgroundProperty, BlockBrush(kind));
        AutomationProperties.SetName(glyph, description);
        return glyph;
    }

    public static Button CreateBlockContext(out TextBlock text)
    {
        text = new TextBlock
        {
            FontFamily = InterfaceFont, FontSize = DefaultMetrics.Body,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        var surface = CreateButton(string.Empty, DefaultMetrics);
        surface.Content = text;
        surface.Padding = new Thickness(8, 4, 8, 4);
        surface.ToolTip = "點擊返回區塊起始行";
        surface.Cursor = Cursors.Hand;
        surface.Focusable = false;
        surface.IsTabStop = false;
        surface.WithTheme(Control.ForegroundProperty, ThemeBrush.BlockHintForeground)
            .WithTheme(Control.BackgroundProperty, ThemeBrush.BlockHintBackground)
            .WithTheme(Control.BorderBrushProperty, ThemeBrush.Block);
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "bg" };
        border.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BlockHintBackground);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Block);
        border.SetResourceReference(TextElement.ForegroundProperty, ThemeBrush.BlockHintForeground);
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 0));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(InnerRadius));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        foreach (var property in new[] { UIElement.IsMouseOverProperty, ButtonBase.IsPressedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            // 保留不透明淡底，透過手形與經對比校正的連結色提示導覽。
            trigger.Setters.Add(ThemeResourceSet.Setter(TextElement.ForegroundProperty, ThemeBrush.BlockHintHoverForeground, "bg"));
            template.Triggers.Add(trigger);
        }
        surface.Template = template;
        return surface;
    }
}
