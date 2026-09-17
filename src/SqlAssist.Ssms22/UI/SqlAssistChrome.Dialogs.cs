using System;
using System.Collections.Generic;
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

    /// <summary>對話框內容與視窗邊緣的距離；原生 Titlebar 之下四邊一致。</summary>
    public static readonly Thickness DialogPadding = new(16);

    /// <summary>
    /// 對話框頁尾：左側摘要或狀態吃剩餘寬度，右側動作依序排列、間距 8。
    /// </summary>
    /// <param name="leading">左側內容；沒有摘要時傳 null。</param>
    /// <param name="actions">由左到右；主要動作放最後。</param>
    public static DockPanel CreateDialogFooter(UIElement? leading, params Button[] actions) =>
        CreateDialogFooter(Array.Empty<Button>(), leading, actions);

    /// <summary>
    /// 對話框頁尾：左側附屬動作、中間狀態、右側結束動作；三組都在同一列，只有狀態吃剩餘寬度。
    /// </summary>
    /// <param name="utilities">不結束對話框的附屬動作（複製、開啟位置…），一律幽靈按鈕，由左到右。</param>
    /// <param name="leading">狀態或摘要；沒有時傳 null。</param>
    /// <param name="actions">結束對話框的動作，由左到右；主要動作放最後。</param>
    public static DockPanel CreateDialogFooter(IReadOnlyList<Button> utilities, UIElement? leading, params Button[] actions)
    {
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        // 視覺樹依閱讀順序加入：附屬動作 → 結束動作，Tab 才會由左到右。
        if (utilities.Count > 0) footer.Children.Add(CreateDialogButtonRow(utilities, Dock.Left));
        footer.Children.Add(CreateDialogButtonRow(actions, Dock.Right));
        if (leading is FrameworkElement element)
        {
            element.Margin = new Thickness(utilities.Count > 0 ? 16 : 0, 0, 16, 0);
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        if (leading is not null) footer.Children.Add(leading);
        return footer;
    }

    private static StackPanel CreateDialogButtonRow(IReadOnlyList<Button> buttons, Dock dock)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        for (var i = 0; i < buttons.Count; i++)
        {
            buttons[i].MinWidth = Math.Max(buttons[i].MinWidth, DialogButtonMinWidth);
            buttons[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            row.Children.Add(buttons[i]);
        }
        DockPanel.SetDock(row, dock);
        return row;
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

    /// <summary>卡片內距；主卡片與分段卡片共用，卡片內文字落在同一條左軸線。</summary>
    public static readonly Thickness CardPadding = new(12);

    /// <summary>
    /// 分段卡片：淡色小標在卡片外、內容放進一塊表面；小標內縮到卡片內文字的左軸線。
    /// </summary>
    /// <remarks>
    /// 給像儀表板那樣由多個獨立區塊組成的頁面：每塊是一張卡，不在卡片裡再套卡片。
    /// 內容只放在單一表面上時（對話框表單）仍用 <see cref="CreateSection"/>，不為每段加框。
    /// </remarks>
    public static StackPanel CreateCardSection(string title, UIElement body, bool first = false)
    {
        var card = CreateSurface(body);
        card.Padding = CardPadding;
        var section = CreateSection(title, card, first);
        // 小標與卡片內文字對齊：內距加上 1 DIP 外框。
        ((FrameworkElement)section.Children[0]).Margin = new Thickness(CardPadding.Left + 1, 0, 0, 8);
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
