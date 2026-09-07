using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlAssistChrome
{
    public static ThemeBrush BlockBrush(BlockKind kind) => kind switch
    {
        BlockKind.Try => ThemeBrush.BlockTry, BlockKind.Catch => ThemeBrush.BlockCatch,
        BlockKind.Case => ThemeBrush.BlockCase, BlockKind.Parenthesis => ThemeBrush.BlockParenthesis,
        BlockKind.Bracket => ThemeBrush.BlockBracket, _ => ThemeBrush.Block
    };

    public static Border CreateBlockGlyph(BlockKind kind, double height, string description)
    {
        var glyph = new Border { Width = 3, Height = Math.Max(1, height), ToolTip = description }
            .WithTheme(Border.BackgroundProperty, BlockBrush(kind));
        AutomationProperties.SetName(glyph, description);
        VsThemeBrushes.Apply(glyph);
        return glyph;
    }

    public static Border CreateBlockContext(out TextBlock text)
    {
        text = new TextBlock
        {
            FontFamily = InterfaceFont, FontSize = DefaultMetrics.Body,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        var surface = new Border
        {
            Child = text, Padding = new Thickness(8, 4, 8, 4),
            IsHitTestVisible = false, CornerRadius = new CornerRadius(InnerRadius)
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.ListBackground);
        VsThemeBrushes.Apply(surface);
        return surface;
    }
}
