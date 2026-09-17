using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 對話框的共用版面：分段標題、資訊列、選項列與單一頁尾。
/// </summary>
/// <remarks>
/// 標題列交給原生 Titlebar，內容由上而下是「資訊列 → 分段 → 頁尾」。每一塊只有一種做法，
/// 新對話框照著組就和既有的對話框同一個節奏，不在呼叫端各自調邊距與按鈕寬度。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <summary>頁尾按鈕的最小寬度；「取消」與動作按鈕等寬，不因文字長短一高一低。</summary>
    public const double DialogButtonMinWidth = 80;

    /// <summary>
    /// 對話框頁尾：左側摘要或狀態吃剩餘寬度，右側動作依序排列、間距 8。
    /// </summary>
    /// <param name="leading">左側內容；沒有摘要時傳 null。</param>
    /// <param name="actions">由左到右；主要動作放最後。</param>
    public static DockPanel CreateDialogFooter(UIElement? leading, params Button[] actions)
    {
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        for (var i = 0; i < actions.Length; i++)
        {
            actions[i].MinWidth = Math.Max(actions[i].MinWidth, DialogButtonMinWidth);
            actions[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            buttons.Children.Add(actions[i]);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        if (leading is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 0, 16, 0);
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        if (leading is not null) footer.Children.Add(leading);
        return footer;
    }

    /// <summary>破壞性的主要動作：靜止就是語意色淡底，文字寫明動作與數量；不得設為預設按鈕。</summary>
    public static Button CreateDangerButton(string text)
    {
        var button = CreateButton(text, DefaultMetrics, primary: true);
        button.Template = CreateButtonTemplate(primary: true, SqlActionTone.Danger);
        var style = new Style(typeof(Button));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.DangerForeground));
        button.Style = style;
        return button;
    }

    /// <summary>分段：淡色小標在上、內容在下，間距 8；分段之間距離 16，第一段不留上緣。對話框與用量分頁共用。</summary>
    public static StackPanel CreateSection(string title, UIElement body, bool first = false)
    {
        var section = new StackPanel { Margin = new Thickness(0, first ? 0 : 16, 0, 0) };
        var label = CreateLabel(title, DefaultMetrics);
        label.Margin = new Thickness(0, 0, 0, 8);
        label.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);
        section.Children.Add(label);
        section.Children.Add(body);
        AutomationProperties.SetName(section, title);
        return section;
    }

    /// <summary>內容上緣的一條提示：語意圖示＋可換行文字，極淡底色，不另立標題。</summary>
    public static Border CreateInfoBar(SqlIcon icon, string text)
    {
        var content = new DockPanel();
        var glyph = CreateIcon(icon);
        glyph.Margin = new Thickness(0, 1, 8, 0); glyph.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(glyph, Dock.Left); content.Children.Add(glyph);
        content.Children.Add(new TextBlock
        {
            Text = text, FontFamily = InterfaceFont, FontSize = DefaultMetrics.Caption, TextWrapping = TextWrapping.Wrap
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground));
        var bar = new Border
        {
            Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(InnerRadius + 1),
            Padding = new Thickness(12, 8, 12, 8)
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.BadgeBackground).WithTheme(Border.BorderBrushProperty, ThemeBrush.Hairline);
        AutomationProperties.SetName(bar, text);
        return bar;
    }

    /// <summary>一組選項列共用一塊表面；列與列之間靠停駐底色區分，不畫分隔線。</summary>
    public static Border CreateOptionGroup(params UIElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows) stack.Children.Add(row);
        var surface = CreateSurface(stack);
        surface.Padding = new Thickness(4);
        return surface;
    }

    /// <summary>
    /// 選項列：核取方塊、標題與一行淡色說明；整列都能點，右側可放附屬控制項。
    /// </summary>
    /// <remarks>
    /// 說明放進核取方塊的內容，點說明文字也會切換，鍵盤焦點與自動化名稱仍落在核取方塊本身。
    /// 說明只有一行，被省略時從 Tooltip 讀完整內容。
    /// </remarks>
    /// <param name="accessory">只屬於這個選項的附屬設定；呼叫端依勾選狀態決定是否啟用。</param>
    public static Border CreateOptionRow(CheckBox box, string title, string description, FrameworkElement? accessory = null)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = title, FontFamily = InterfaceFont, FontSize = DefaultMetrics.Body, TextTrimming = TextTrimming.CharacterEllipsis
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground));
        var detail = CreateStatusText(DefaultMetrics);
        detail.Text = description; detail.Margin = new Thickness(0, 1, 0, 0);
        text.Children.Add(detail);
        box.Content = text;
        box.Template = CreateCheckBoxTemplate();
        box.FontFamily = InterfaceFont; box.FontSize = DefaultMetrics.Body;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.FocusVisualStyle = null;
        AutomationProperties.SetName(box, title);
        AutomationProperties.SetHelpText(box, description);

        var layout = new DockPanel();
        if (accessory is not null)
        {
            accessory.Margin = new Thickness(12, 0, 0, 0);
            accessory.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(accessory, Dock.Right);
            layout.Children.Add(accessory);
        }
        layout.Children.Add(box);

        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.RowHover));
        style.Triggers.Add(hover);
        var row = new Border
        {
            Child = layout, Style = style, CornerRadius = new CornerRadius(InnerRadius), Padding = new Thickness(10, 7, 10, 7)
        };
        // 列的留白也算選項的命中範圍；核取方塊與附屬控制項自己處理的點擊不重複切換。
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled || !box.IsEnabled) return;
            if (accessory is not null && e.OriginalSource is DependencyObject source && accessory.IsAncestorOf(source)) return;
            box.IsChecked = box.IsChecked != true;
            e.Handled = true;
        };
        return row;
    }
}
