using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using Microsoft.VisualStudio.Imaging;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 浮動預覽專屬的外觀。
/// </summary>
/// <remarks>
/// 字型、字級、按鈕、分頁與資料格的樣板都在 <see cref="SqlAssistChrome"/>，
/// 那是整個擴充共用的一套；這裡只留下別的視窗用不到的部分——出現時的動畫、
/// 物件圖示控制項的排版，以及欄位表的旗標徽章。
/// </remarks>
internal static class PreviewChrome
{
    /// <summary>主索引鍵徽章要換成強調色，比對的就是這個字串。</summary>
    public static readonly string PrimaryKeyFlag = SqlColumnFlag.PrimaryKey.ToDisplayName();

    /// <summary>
    /// 出現時的淡入。
    /// </summary>
    /// <remarks>
    /// 只做透明度，不做縮放。承載視窗的大小是平台按內容量出來的，縮放只會讓
    /// 內容縮進去而視窗不動，四周露出一圈承載視窗的底色——那比不做動畫還糟。
    /// 120 毫秒、只用 ease-out、不回彈：短到不擋操作，但足以讓人看出它是
    /// 「長出來」而不是「跳出來」。
    /// </remarks>
    public static void PlayAppear(UIElement element)
    {
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // 圖示只輔助辨識，種類文字仍保留在淡色摘要；每個預覽持有自己的控制項。
    public static CrispImage CreateObjectIcon()
    {
        return new CrispImage
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Moniker = SqlIcons.GetMoniker(SqlObjectKind.Unknown)
        };
    }

    /// <summary>
    /// 一列旗標徽章。
    /// </summary>
    /// <remarks>
    /// NULL、PK、IDENTITY 原本是三個獨立的文字欄，每一列都要為那三欄留寬度，
    /// 而絕大多數格子是空的。收成一欄膠囊之後欄位表從八欄變成六欄，
    /// 空的地方就真的什麼都不畫。
    ///
    /// 只標例外：可為 NULL 是 SQL 的預設，因此不給徽章——沒有徽章就是可為 NULL。
    /// </remarks>
    public static DataTemplate CreateFlagsCellTemplate(string path, SqlAssistChrome.Metrics metrics)
    {
        var chip = new FrameworkElementFactory(typeof(Border)) { Name = "chip" };
        chip.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground);
        chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        chip.SetValue(Border.PaddingProperty, new Thickness(6, 0, 6, 1));
        chip.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        chip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var text = new FrameworkElementFactory(typeof(TextBlock)) { Name = "chipText" };
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.FontFamilyProperty, SqlAssistChrome.InterfaceFont);
        text.SetValue(TextBlock.FontSizeProperty, metrics.Badge);
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        chip.AppendChild(text);

        var chipTemplate = new DataTemplate { VisualTree = chip };

        // 整個視窗只有主索引鍵用強調色。多給一個，強調就不再是強調。
        var primaryKey = new DataTrigger { Binding = new Binding(), Value = PrimaryKeyFlag };
        primaryKey.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.AccentBackground, "chip"));
        primaryKey.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder, "chip"));
        primaryKey.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "chip"));
        primaryKey.Setters.Add(ThemeResourceSet.Setter(TextBlock.ForegroundProperty, ThemeBrush.ListForeground, "chipText"));
        chipTemplate.Triggers.Add(primaryKey);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var items = new FrameworkElementFactory(typeof(ItemsControl));
        items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(path));
        items.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(panel));
        items.SetValue(ItemsControl.ItemTemplateProperty, chipTemplate);
        items.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 6, 0));
        items.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        return new DataTemplate { VisualTree = items };
    }

    /// <summary>
    /// 把欄位的性質整理成徽章文字；沒有例外時回傳空清單。
    /// </summary>
    /// <remarks>
    /// 計算欄位不給徽章：它在表格裡自己就是一整欄，運算式本身比徽章說得更多。
    /// </remarks>
    public static IReadOnlyList<string> BuildFlags(SqlColumnInfo column)
    {
        var badges = new List<string>(3);

        foreach (var flag in SqlColumnPresentation.Flags(column))
        {
            if (flag != SqlColumnFlag.Computed)
            {
                badges.Add(flag.ToDisplayName());
            }
        }

        return badges;
    }
}
