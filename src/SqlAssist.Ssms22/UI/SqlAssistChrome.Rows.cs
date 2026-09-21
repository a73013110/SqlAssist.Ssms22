using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 清單列與按鈕內容的共用元件：SQL Memory、SQL Search 與之後的工具窗都從這裡取。
/// </summary>
/// <remarks>
/// 放在中性的 partial 而不是某一個功能的那一份：命名與位置也是介面的一部分，留在
/// <c>SqlAssistChrome.SqlMemory.cs</c> 的話，下一個視窗仍然得去 Memory 那一份取膠囊，
/// 而它與 Memory 的領域語意其實一點關係都沒有。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <summary>
    /// 主要名稱的寬度上限。
    /// </summary>
    /// <remarks>
    /// 工具窗停在右側時整列只有約 300 DIP，而名稱那一組先量。不設上限的話，一個長名稱就會把
    /// 伺服器、資料庫與時間整組擠出這一列；被省略的中段仍讀得到，在 Tooltip 與 Preview。
    /// </remarks>
    internal const double RowNameMaxWidth = 180d;

    // Content 的邏輯父層一定是所屬 Control；不能依賴尚未建立或重掛的樣板視覺祖先。
    // 狀態色在 Control 的共用樣板處切換，也不受宿主 ContentPresenter 隱含樣式影響。
    private static Binding OwnerForeground() => new Binding
    {
        Path = new PropertyPath(Control.ForegroundProperty),
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
    };

    public static TextBlock CreateButtonText(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetBinding(TextBlock.ForegroundProperty, OwnerForeground()); return label;
    }

    /// <summary>圖示加文字的按鈕內容；分頁、工具列按鈕與膠囊選擇器共用同一條視覺中心線。</summary>
    public static DockPanel CreateIconLabel(SqlIcon icon, string text)
    {
        var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var glyph = CreateIcon(icon); glyph.Margin = new Thickness(0, 0, 5, 0);
        DockPanel.SetDock(glyph, Dock.Left); content.Children.Add(glyph);
        content.Children.Add(CreateButtonText(text));
        return content;
    }

    /// <summary>清單列的第一列：左右兩組各自靠邊，中間留彈性空白。</summary>
    /// <remarks>
    /// <c>LastChildFill</c> 一定要關掉：拉滿最後一個子項就吃掉那一段空白，兩組會黏成一組。
    /// </remarks>
    internal static FrameworkElementFactory CreateRowLine()
    {
        var line = new FrameworkElementFactory(typeof(DockPanel));
        line.SetValue(DockPanel.LastChildFillProperty, false);
        return line;
    }

    /// <summary>清單列第一列的左半：主要名稱固定最左，狀態／類型與次要標記緊跟在它右邊。</summary>
    /// <remarks>
    /// 靠左對齊才只量自己的寬度；拉滿的話後面那幾顆膠囊會被推到右半那一組旁邊，讀起來像同一組，
    /// 中間也不再有彈性空白。名稱是最後一個子項（fill），剩多少吃多少並 ellipsis。
    /// </remarks>
    private static FrameworkElementFactory RowIdentityGroup()
    {
        var group = new FrameworkElementFactory(typeof(DockPanel));
        group.SetValue(DockPanel.DockProperty, Dock.Left);
        group.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        return group;
    }

    /// <summary>
    /// 開一列清單列的第一列骨架：疊層、左右兩組、浮在右緣的操作層與窄版降級。
    /// </summary>
    /// <remarks>
    /// 欄位與順序仍由各功能自己決定，這裡只固定四條一寫錯就回歸、而且<b>只在特定情況才看得出來</b>
    /// 的規則（見 <see cref="SqlRowHeading"/>）。兩份樣板各寫一次的那一版，差異是慢慢長出來的：
    /// 同一段骨架抄兩份之後，下一個人只會改他手上那一份。
    ///
    /// 做成組裝步驟而不是「欄位由 descriptor 決定」的通用 builder：那等於再發明一次
    /// <see cref="DataTemplate"/>，而兩邊的欄位本來就不是同一種東西——搜尋的名稱是帶高亮區段的
    /// <see cref="SqlHighlightText"/>，SQL Memory 的是純文字。
    /// </remarks>
    /// <param name="lines">這一列的內容堆疊；第一列會 append 進去，內容列由呼叫端接著加。</param>
    internal static SqlRowHeading BeginRowHeading(FrameworkElementFactory lines)
    {
        var heading = CreateRowLine();
        // 內容與操作層疊在同一列上：層不參與量測，所以底下那一列一直排到滿，揭露也不動版面。
        var layers = new FrameworkElementFactory(typeof(Grid));
        layers.AppendChild(heading);
        lines.AppendChild(layers);
        return new SqlRowHeading(heading, layers, RowIdentityGroup(), NarrowRowTrigger());
    }

    private static FrameworkElementFactory BoundText(string property)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property)); text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(property));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.ForegroundProperty, OwnerForeground());
        return text;
    }

    /// <summary>
    /// 帶一個 16 DIP 圖示插槽的中性膠囊。
    /// </summary>
    /// <remarks>
    /// 圖示與文字各給一個名字（<c>name + "Icon"</c>／<c>name + "Text"</c>），窄版才降得成
    /// icon-only：整顆收掉會讓那一列少講一件事，而降級只是把字收進 Tooltip。
    /// </remarks>
    /// <param name="categoryProperty">
    /// 搜尋分類識別字；provider 宣告的分類是資料不是列舉，所以與 <paramref name="iconProperty"/> 分開。
    /// </param>
    private static FrameworkElementFactory CreateBadge(string property, string name, SqlIcon? icon = null,
        string? iconProperty = null, string? categoryProperty = null, string? toolTipProperty = null)
    {
        var badge = new FrameworkElementFactory(typeof(Border)) { Name = name };
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        badge.SetValue(Border.PaddingProperty, new Thickness(6, 1, 6, 1)); badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetValue(FrameworkElement.MaxWidthProperty, 180d);
        badge.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground); badge.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        // 降成 icon-only 之後，那幾個字唯一的去處是 Tooltip 與自動化名稱。
        badge.SetBinding(FrameworkElement.ToolTipProperty, new Binding(toolTipProperty ?? property));
        badge.SetBinding(AutomationProperties.NameProperty, new Binding(toolTipProperty ?? property));
        var content = new FrameworkElementFactory(typeof(DockPanel));
        var glyph = new FrameworkElementFactory(typeof(SqlIconImage)) { Name = name + "Icon" };
        if (categoryProperty is not null) glyph.SetBinding(SqlIconImage.CategoryIdProperty, new Binding(categoryProperty));
        else if (iconProperty is not null) glyph.SetBinding(SqlIconImage.IconProperty, new Binding(iconProperty));
        else glyph.SetValue(SqlIconImage.IconProperty, icon);
        glyph.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.AppendChild(glyph);
        var text = BoundText(property); text.Name = name + "Text"; text.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        content.AppendChild(text); badge.AppendChild(content); return badge;
    }

    /// <summary>
    /// 沒有圖示的精簡中性膠囊：執行次數與搜尋結果的命中部位共用。
    /// </summary>
    /// <remarks>
    /// 不借用 <see cref="CreateBadge"/>：那一份一定留一個 16 DIP 的圖示插槽，而沒有圖示的膠囊
    /// 會因此在字的左邊空出一整格，一列擠三顆就看得出來。外框、圓角與高度兩者相同。
    /// </remarks>
    /// <param name="toolTipProperty">Tooltip 與自動化名稱讀的屬性；null 表示沿用膠囊上的字。</param>
    private static FrameworkElementFactory CreateTextBadge(string property, string name, string? toolTipProperty = null)
    {
        var badge = new FrameworkElementFactory(typeof(Border)) { Name = name };
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        badge.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1)); badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetResourceReference(Border.BackgroundProperty, ThemeBrush.BadgeBackground); badge.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.Hairline);
        badge.SetBinding(AutomationProperties.NameProperty, new Binding(toolTipProperty ?? property));
        var text = BoundText(property); text.Name = name + "Text"; text.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(toolTipProperty ?? property));
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        badge.AppendChild(text);
        return badge;
    }

    /// <summary>列上的幽靈操作按鈕；卡片與版本時間軸共用尺寸、色調與前景跟隨規則。</summary>
    /// <param name="action">按鈕的 Tag；清單以它派送，不拿圖示或文字當識別。</param>
    internal static FrameworkElementFactory CreateRowActionButton(string name, object action, SqlIcon icon, string label,
        SqlActionTone tone, bool separated)
    {
        var button = new FrameworkElementFactory(typeof(Button)) { Name = name };
        button.SetValue(FrameworkElement.TagProperty, action); button.SetValue(FrameworkElement.ToolTipProperty, label);
        button.SetValue(AutomationProperties.NameProperty, label);
        button.SetValue(Control.TemplateProperty, CreateGhostButtonTemplate(tone)); button.SetValue(Control.PaddingProperty, new Thickness(3));
        // 動作列不再有實色底，前景必須跟隨卡片的 hover／selected 配對色（尤其高對比）。
        button.SetBinding(Control.ForegroundProperty, OwnerForeground());
        button.SetValue(FrameworkElement.WidthProperty, 24d); button.SetValue(FrameworkElement.HeightProperty, 22d);
        if (separated) button.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        var glyph = new FrameworkElementFactory(typeof(SqlIconImage)); glyph.SetValue(SqlIconImage.IconProperty, icon);
        button.AppendChild(glyph);
        return button;
    }

    /// <summary>操作層左緣那一段淡出的寬度；蓋住的那顆膠囊要淡掉，不是被一條直邊切掉。</summary>
    private const double RowActionFade = 24d;

    /// <summary>操作層的淡出遮罩；只做一次並凍結，每一列共用同一份。</summary>
    private static readonly Brush RowActionFadeMask = CreateRowActionFadeMask();

    /// <summary>
    /// 只有<b>左緣那 <see cref="RowActionFade"/> DIP</b> 淡出，其餘整片不透明。
    /// </summary>
    /// <remarks>
    /// 一定要用 <see cref="BrushMappingMode.Absolute"/>。相對座標的那一版兩個停駐點落在 0 與 1，
    /// 也就是整個操作層由左到右從全透明線性升到不透明——整排圖示是半透明的，底下的連線膠囊
    /// 一路透出來疊在上面，而那正是「停駐時功能與後面的東西糊在一起」的樣子。
    /// 絕對座標讓漸層在 <see cref="RowActionFade"/> DIP 處就結束，之後由
    /// <see cref="GradientSpreadMethod.Pad"/> 補成實心，所以只有交界那一小段是軟的。
    /// 遮罩是固定的灰階，不隨主題也不隨列寬變，做一次凍結共用。
    /// </remarks>
    private static Brush CreateRowActionFadeMask()
    {
        var mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(RowActionFade, 0),
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Pad
        };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1));
        mask.Freeze();
        return mask;
    }

    /// <summary>
    /// 列上停駐才出現的操作層：浮在第一列右緣，<b>不佔</b>版面寬度。
    /// </summary>
    /// <remarks>
    /// 早期版本讓動作區留在第一列裡並用 <see cref="Visibility.Hidden"/> 預留寬度，理由是
    /// 停駐不得改變版面尺寸——那條規矩仍然成立，但代價是每一列右邊永遠空著一塊近百 DIP 的
    /// 空白，而它在停靠面板裡等於名稱少掉三分之一。浮在上層兩件事都要得到：層本身不參與
    /// 量測，所以揭露與收起都不動版面；底下那一列則一直排到滿。
    ///
    /// 層是不透明的（<see cref="ThemeBrush.ListBackground"/> 加一層跟著列狀態走的染色，
    /// 見 <see cref="MirrorRowStateOnActions"/>），否則被蓋住的膠囊會透出來疊在圖示上。
    /// 左緣用 <see cref="UIElement.OpacityMask"/> 淡出而不是硬邊：被蓋住的膠囊多半只被切掉半個字，
    /// 而一條直邊看起來像畫壞了。遮罩是固定的灰階，不隨主題變，所以做一次凍結共用。
    /// </remarks>
    /// <param name="actions">這一列的動作那一排；宿主自己決定放哪幾顆與窄版收哪幾顆。</param>
    /// <param name="name">層的名字；揭露的 trigger 指的就是它，兩份清單共用同一個預設值。</param>
    internal static FrameworkElementFactory CreateRowActionLayer(
        FrameworkElementFactory actions, string name = "actions")
    {
        var layer = new FrameworkElementFactory(typeof(Border)) { Name = name };
        layer.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        layer.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        layer.SetValue(UIElement.OpacityMaskProperty, RowActionFadeMask);
        layer.SetResourceReference(Border.BackgroundProperty, ThemeBrush.ListBackground);
        // Collapsed 而不是 Hidden：層不佔寬度，收起來也不會在右邊留一塊空白。
        layer.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

        // 揭露時從右緣滑進來；位移走 RenderTransform，層本身又不參與量測，版面兩次都不動。
        layer.SetValue(UIElement.RenderTransformProperty, new TranslateTransform());

        var tint = new FrameworkElementFactory(typeof(Border)) { Name = name + "Tint" };
        tint.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        layer.AppendChild(tint);

        actions.SetValue(FrameworkElement.MarginProperty, new Thickness(RowActionFade, 0, 0, 0));
        actions.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        tint.AppendChild(actions);
        return layer;
    }

    /// <summary>操作層揭露的長度；揭露動畫這一級與內容表面出現同一個數字。</summary>
    internal static readonly System.TimeSpan RowActionRevealDuration = System.TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// 停駐或鍵盤走到這一列就揭露操作層。
    /// </summary>
    /// <remarks>
    /// 兩份清單共用這一份，而不是各自寫一次同樣的 trigger 迴圈：揭露條件（滑鼠<b>或</b>鍵盤焦點）
    /// 與動畫是同一件事的兩半，分開寫的下場是其中一邊加了動畫、另一邊沒有，
    /// 而只用鍵盤的人看到的是一整片直接閃出來的圖示。
    ///
    /// 動畫只做淡入與 6 DIP 的水平位移，不縮放：層蓋住的是連線膠囊，讓它<b>從右緣滑進來</b>
    /// 才說得出「這是浮在上面的一塊東西」，而不是那幾顆膠囊突然變成了圖示。
    /// <see cref="FillBehavior.Stop"/> 讓它結束就回到基底值，收起之後下一次仍從頭播。
    /// </remarks>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    internal static void RevealRowActions(DataTemplate template, string name = "actions", bool? motion = null)
    {
        var animate = motion ?? MotionEnabled;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();

        foreach (var property in new[] { nameof(UIElement.IsMouseOver), nameof(UIElement.IsKeyboardFocusWithin) })
        {
            var reveal = new DataTrigger
            {
                Binding = new Binding(property)
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
                },
                Value = true
            };
            reveal.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, name));
            if (animate)
            {
                var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
                var fade = new DoubleAnimation
                {
                    From = 0, To = 1, Duration = RowActionRevealDuration, EasingFunction = ease
                };
                Storyboard.SetTargetName(fade, name);
                Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
                var slide = new DoubleAnimation
                {
                    From = 6, To = 0, Duration = RowActionRevealDuration, EasingFunction = ease
                };
                Storyboard.SetTargetName(slide, name);
                Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
                storyboard.Children.Add(fade);
                storyboard.Children.Add(slide);
                reveal.EnterActions.Add(new BeginStoryboard { Storyboard = storyboard });
            }

            template.Triggers.Add(reveal);
        }
    }

    /// <summary>
    /// 操作層的底色跟著列的停駐與選取走。
    /// </summary>
    /// <remarks>
    /// 不跟著的症狀是停駐時右邊浮出一塊沒有染色的方塊，而那正是使用者眼睛看的地方。
    /// 選取寫在停駐之後：兩個條件同時成立時，後宣告的那一個才是列自己畫的那一種。
    /// 順序與 <c>CreateSqlCardStyle</c> 的那一組相同，兩邊不一致就會差一階。
    /// </remarks>
    internal static void MirrorRowStateOnActions(DataTemplate template, string name = "actions")
    {
        foreach (var (property, brush) in new[]
                 {
                     (nameof(UIElement.IsMouseOver), ThemeBrush.RowHover),
                     (nameof(ListBoxItem.IsSelected), ThemeBrush.RowSelected)
                 })
        {
            var trigger = new DataTrigger
            {
                Binding = new Binding(property)
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
                },
                Value = true
            };
            trigger.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, brush, name + "Tint"));
            template.Triggers.Add(trigger);
        }
    }

    /// <summary>窄版的 overflow：打開這一列本來就有的快捷選單，不另建第二份命令清單。</summary>
    /// <remarks>
    /// 沒有 Tag，清單的派送不認得它；命令由選單自己送，與右鍵走同一條路，
    /// 新增一個列操作仍然只改一處 <c>RowCommand.All</c>。
    /// </remarks>
    internal static FrameworkElementFactory CreateRowOverflowButton(string name = "overflow")
    {
        var button = new FrameworkElementFactory(typeof(Button)) { Name = name };
        button.SetValue(FrameworkElement.ToolTipProperty, OverflowLabel);
        button.SetValue(AutomationProperties.NameProperty, OverflowLabel);
        button.SetValue(Control.TemplateProperty, CreateGhostButtonTemplate());
        button.SetValue(Control.PaddingProperty, new Thickness(3));
        button.SetBinding(Control.ForegroundProperty, OwnerForeground());
        button.SetValue(FrameworkElement.WidthProperty, 24d); button.SetValue(FrameworkElement.HeightProperty, 22d);
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        // 一般版放得下整組操作；只有窄版的 trigger 才把它顯示出來。
        button.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        var glyph = new FrameworkElementFactory(typeof(SqlIconImage)); glyph.SetValue(SqlIconImage.IconProperty, SqlIcon.Overflow);
        button.AppendChild(glyph);
        button.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OpenRowMenu));
        return button;
    }

    /// <summary>overflow 的名稱；Tooltip、自動化名稱與測試共用同一份字。</summary>
    internal const string OverflowLabel = "更多操作";

    private static void OpenRowMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;

        var item = FindAncestor<ListBoxItem>(button);
        if (item is null || ItemsControl.ItemsControlFromItemContainer(item) is not ListBox list ||
            list.ContextMenu is not { } menu) return;

        // 與右鍵同一條路：先選取這一列，選單的命令才作用在使用者按的那一筆上。
        item.IsSelected = true;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        args.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    /// <summary>窄版降級的 trigger；降哪幾樣由呼叫端依自己那一列的優先序決定。</summary>
    /// <remarks>
    /// 讀的是繼承下來的 <see cref="SqlRowLayout.WidthModeProperty"/>，所以只要問最近的那一個
    /// 祖先元素就夠——樣板不必認得宿主是清單、是 Preview 的資訊列，還是測試的裸
    /// <c>ContentControl</c>；沒有人設過就是一般版。
    /// </remarks>
    internal static DataTrigger NarrowRowTrigger() => new()
    {
        Binding = new Binding
        {
            Path = new PropertyPath(SqlRowLayout.WidthModeProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(FrameworkElement), 1)
        },
        Value = SqlRowWidth.Narrow
    };

    /// <summary>把一顆膠囊降成 icon-only：字收進 Tooltip，圖示右邊的間距一起收掉。</summary>
    internal static void IconOnlyInNarrow(DataTrigger narrow, string badge)
    {
        narrow.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, badge + "Text"));
        narrow.Setters.Add(new Setter(FrameworkElement.MarginProperty, default(Thickness), badge + "Icon"));
    }
}

/// <summary>
/// 組裝中的清單列第一列；由 <see cref="SqlAssistChrome.BeginRowHeading"/> 建立，
/// 填完欄位之後用 <see cref="Complete"/> 收尾。
/// </summary>
/// <remarks>
/// 它擔保的是四條規則，每一條寫錯都只在特定情況下才看得出來：
///
/// <list type="number">
/// <item>靠右那幾組<b>先</b>宣告。<see cref="DockPanel"/> 依宣告順序量測，名稱先量的話，一個長名稱
/// 會把連線膠囊與時間整組擠出這一列——而它們是固定寬的，讓得起的只有可以 ellipsis 的名稱。
/// 所以 <see cref="Identity"/> 由 <see cref="Complete"/> 最後才掛上 <see cref="Heading"/>。</item>
/// <item>操作是浮在第一列右緣的<b>疊層</b>，不自己占一列，也不用 <see cref="Visibility.Hidden"/>
/// 預留寬度。</item>
/// <item>overflow 那一顆在窄版才出現，而它開的是這一列本來就有的快捷選單。</item>
/// <item>底色鏡射（<see cref="SqlAssistChrome.MirrorRowStateOnActions"/>）宣告在揭露
/// （<see cref="SqlAssistChrome.RevealRowActions"/>）<b>之前</b>。</item>
/// </list>
///
/// 窄版那一條 trigger 由 <see cref="Complete"/> 最後才加進樣板，所以它排在功能自己那幾條後面：
/// 兩條 trigger 同時成立而且碰同一個屬性時，後宣告的贏——窄到要降級了還被別的條件蓋回去，
/// 症狀是那一列在 300 DIP 下仍然排著三顆按鈕。
/// </remarks>
internal sealed class SqlRowHeading
{
    private readonly FrameworkElementFactory _layers;

    internal SqlRowHeading(FrameworkElementFactory heading, FrameworkElementFactory layers,
        FrameworkElementFactory identity, DataTrigger narrow)
    {
        Heading = heading;
        _layers = layers;
        Identity = identity;
        Narrow = narrow;
        Actions = new FrameworkElementFactory(typeof(StackPanel));
        Actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
    }

    /// <summary>第一列本身；<b>只</b>把靠右那幾組 append 進來，左半走 <see cref="Identity"/>。</summary>
    public FrameworkElementFactory Heading { get; }

    /// <summary>左半：主要名稱與緊跟著它的狀態、類型與次要標記。</summary>
    public FrameworkElementFactory Identity { get; }

    /// <summary>操作那一排；overflow 不必自己加，<see cref="Complete"/> 會接在最後。</summary>
    public FrameworkElementFactory Actions { get; }

    /// <summary>窄版降級的 trigger；降哪幾樣由呼叫端依自己那一列的優先序決定。</summary>
    public DataTrigger Narrow { get; }

    /// <summary>收尾：掛上左半、補 overflow、把操作疊上去，並接上鏡射與揭露。</summary>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public void Complete(DataTemplate template, bool? motion = null)
    {
        Heading.AppendChild(Identity);

        var overflow = SqlAssistChrome.CreateRowOverflowButton();
        Actions.AppendChild(overflow);
        Narrow.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, overflow.Name));

        _layers.AppendChild(SqlAssistChrome.CreateRowActionLayer(Actions));
        template.Triggers.Add(Narrow);

        SqlAssistChrome.MirrorRowStateOnActions(template);
        SqlAssistChrome.RevealRowActions(template, motion: motion);
    }
}
