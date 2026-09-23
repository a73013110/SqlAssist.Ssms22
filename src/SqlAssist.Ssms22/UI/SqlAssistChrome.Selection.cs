using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 清單多選的外觀：多選模式才出現的勾選欄、勾起來的列，以及蓋在輸入列上的選取工具列。
/// </summary>
/// <remarks>
/// 放在中性的 partial：SQL Memory 與 SQL Search 用同一份，不在各自的樣板檔裡各畫一次。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <summary>勾選框在列上的名字；揭露的 trigger 與測試都用它找。</summary>
    internal const string RowCheckName = "rowCheck";

    /// <summary>
    /// 「完成」回饋停留多久：動作按鈕的圖示換成打勾，這段時間之後換回來。
    /// </summary>
    /// <remarks>
    /// 不是動畫長度，是讀得到的時間：複製沒有別的看得見的結果，打勾一閃就回去的話使用者會再按一次。
    /// 動畫關閉時照樣換圖示，只是不播彈出。
    /// </remarks>
    internal static readonly TimeSpan SelectionDoneHold = TimeSpan.FromMilliseconds(1200);

    /// <summary>勾選欄的寬度（圓 16 DIP 加與名稱之間 6 DIP）；只在多選模式佔位。</summary>
    internal const double RowCheckColumnWidth = 22d;

    /// <summary>
    /// 把一列的內容包成「勾選欄＋內容」：勾選欄在整列左邊、跨過所有內容列，垂直置中。
    /// </summary>
    /// <remarks>
    /// 平常收起（<see cref="Visibility.Collapsed"/>），版面與沒有多選時完全相同：名稱仍是最左邊那一個。
    /// 只有多選模式才讓出這一欄，那時使用者本來就在逐列勾選，名稱短一截換來一眼看得出哪幾筆勾了。
    /// 疊在狀態膠囊圖示上的那一版省下這一欄，但勾選框長在「執行」旁邊，讀起來像是在勾那個狀態。
    /// </remarks>
    /// <param name="namePath">列名稱的屬性；勾選框的自動化名稱是「選取 名稱」。</param>
    internal static FrameworkElementFactory WrapWithRowCheck(FrameworkElementFactory lines, string namePath)
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        var check = new FrameworkElementFactory(typeof(SqlRowCheckBox)) { Name = RowCheckName };
        check.SetValue(DockPanel.DockProperty, Dock.Left);
        check.SetValue(FrameworkElement.WidthProperty, 16d);
        check.SetValue(FrameworkElement.HeightProperty, 16d);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, RowCheckColumnWidth - 16d, 0));
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(ISqlCheckableRow.IsChecked)) { Mode = BindingMode.OneWay });
        check.SetBinding(AutomationProperties.NameProperty, new Binding(namePath) { StringFormat = "選取 {0}" });
        check.SetValue(FrameworkElement.ToolTipProperty, "勾選或取消；Shift+點擊勾選一段");
        root.AppendChild(check);
        root.AppendChild(lines);
        return root;
    }

    /// <summary>
    /// 勾選欄何時出現：清單開了多選，而且整份清單在多選模式（或這一列已勾選）。
    /// </summary>
    /// <remarks>
    /// 不做每一列的進場動畫：多選模式中捲動時，新產生的容器一套上樣板就會再播一次，
    /// 清單邊捲邊閃。進入模式的回饋由選取工具列的進場負責，那一次只播一次。
    /// </remarks>
    internal static void RevealRowCheck(DataTemplate template)
    {
        var shown = new MultiBinding { Converter = RowCheckVisibility.Instance };
        shown.Bindings.Add(InheritedFlag(SqlRowCheck.IsAvailableProperty));
        shown.Bindings.Add(InheritedFlag(SqlRowCheck.IsActiveProperty));
        shown.Bindings.Add(new Binding(nameof(ISqlCheckableRow.IsChecked)));
        var reveal = new DataTrigger { Binding = shown, Value = true };
        reveal.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, RowCheckName));
        template.Triggers.Add(reveal);
    }

    private static Binding InheritedFlag(DependencyProperty property) => new()
    {
        Path = new PropertyPath(property),
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(FrameworkElement), 1)
    };

    /// <summary>
    /// 勾起來的列：外框換成強調色，底色不動。
    /// </summary>
    /// <remarks>
    /// 焦點列（預覽中的那一筆）用的是選取底色；勾選只換外框，兩件事同時成立時仍分得出哪一筆在預覽。
    /// 外框本來就預留 1 DIP，只換 brush，不動 padding。
    /// </remarks>
    private static void AddCheckedCardTrigger(ControlTemplate template)
    {
        var trigger = new DataTrigger { Binding = new Binding(nameof(ISqlCheckableRow.IsChecked)), Value = true };
        trigger.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder, "card"));
        template.Triggers.Add(trigger);
    }

    /// <summary>
    /// 選取工具列的外殼：與搜尋框同一個圓角與外框，外框用強調色說「現在是多選」。
    /// </summary>
    /// <remarks>
    /// 不透明：它蓋在搜尋列上，透出底下的搜尋字會讓人以為還能打字。
    /// </remarks>
    internal static Border CreateSelectionBarSurface(UIElement content)
    {
        var surface = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0, 2, 0),
            RenderTransform = new TranslateTransform(),
            Visibility = Visibility.Collapsed,
            Opacity = 0
        };
        surface.SetResourceReference(Border.BackgroundProperty, ThemeBrush.ListBackground);
        surface.SetResourceReference(Border.BorderBrushProperty, ThemeBrush.AccentBorder);
        return surface;
    }

    /// <summary>「已選 N 筆」；數字在變，字重比按鈕高一階，一眼就知道選了多少。</summary>
    internal static TextBlock CreateSelectionCount()
    {
        var count = new TextBlock
        {
            FontFamily = InterfaceFont,
            FontSize = DefaultMetrics.Body,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(Spacing.Tight, 0, Spacing.Group, 0)
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        return count;
    }

    /// <summary>工具列上的動作按鈕：圖示加字，幽靈按鈕，停駐才顯色。</summary>
    internal static Button CreateSelectionActionButton(SqlIcon icon, string label, string description, out SqlIconImage glyph)
    {
        var button = CreateButton("", DefaultMetrics);
        var content = CreateIconLabel(icon, label);
        glyph = (SqlIconImage)content.Children[0];
        glyph.RenderTransform = new ScaleTransform(1, 1);
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        button.Content = content;
        button.Padding = new Thickness(6, 3, 8, 3);
        button.MinHeight = 26;
        button.ToolTip = description;
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, description);
        return button;
    }

    /// <summary>工具列上的文字按鈕（全選、取消）；與動作按鈕同高。</summary>
    internal static Button CreateSelectionTextButton(string label, string description)
    {
        var button = CreateButton(label, DefaultMetrics);
        button.Padding = new Thickness(8, 3, 8, 3);
        button.MinHeight = 26;
        button.ToolTip = description;
        AutomationProperties.SetHelpText(button, description);
        return button;
    }

    /// <summary>
    /// 筆數後面那一段淡色說明（全部符合時的「已載入 N 筆」）；放不下時省略，全文在 Tooltip。
    /// </summary>
    /// <remarks>
    /// 與筆數同一列、小一階的淡色字：它補充筆數，不是另一個可以按的東西，所以不做成連結。
    /// </remarks>
    internal static TextBlock CreateSelectionNote()
    {
        var note = CreateHint("", DefaultMetrics);
        note.VerticalAlignment = VerticalAlignment.Center;
        note.TextTrimming = TextTrimming.CharacterEllipsis;
        note.TextWrapping = TextWrapping.NoWrap;
        note.Margin = new Thickness(0, 0, Spacing.Group, 0);
        note.Visibility = Visibility.Collapsed;
        return note;
    }

    /// <summary>
    /// 選取工具列的進出：淡入加 6 DIP 位移，被蓋住的搜尋列同時淡出；可中途反向。
    /// </summary>
    /// <remarks>
    /// 長度沿用 <see cref="RowActionRevealDuration"/>：它也是「浮上來的一層操作」。
    /// 動畫不給起點，從當下的值走向目標，所以進場到一半按 Esc 會從半透明直接退回去，不先跳到全亮。
    /// 結束後交還基底值：保留結束值的動畫會壓過之後的直接指定，「關掉動畫」就會變成關不掉。
    /// </remarks>
    /// <param name="isCurrent">這一次的方向還是不是最新的；反向過的舊動畫走完時不收尾。</param>
    /// <param name="onHidden">退場走完（或沒有動畫時立即）呼叫；呼叫端在這裡把工具列收成 Collapsed。</param>
    internal static void PlaySelectionBar(Border bar, UIElement covered, bool shown, bool? motion, Func<bool> isCurrent,
        Action onHidden)
    {
        var translate = (TranslateTransform)bar.RenderTransform;
        double opacity = shown ? 1 : 0, offset = shown ? 0 : -6, coveredOpacity = shown ? 0 : 1;

        void Complete()
        {
            Settle(bar, UIElement.OpacityProperty, opacity);
            Settle(translate, TranslateTransform.YProperty, offset);
            Settle(covered, UIElement.OpacityProperty, coveredOpacity);
            if (!shown) onHidden();
        }

        if (!(motion ?? MotionEnabled))
        {
            Complete();
            return;
        }

        // 從完全收起開始進場時才從上方 6 DIP 起步；反向的那一次從當下的位置走。
        if (shown && bar.Opacity == 0) Settle(translate, TranslateTransform.YProperty, -6);
        var fade = new DoubleAnimation(opacity, RowActionRevealDuration) { EasingFunction = AppearEase };
        fade.Completed += (_, _) =>
        {
            if (isCurrent()) Complete();
        };
        bar.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(offset, RowActionRevealDuration) { EasingFunction = AppearEase });
        covered.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(coveredOpacity, RowActionRevealDuration) { EasingFunction = AppearEase });
    }

    private static void Settle(Animatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    private static void Settle(UIElement target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    /// <summary>其餘條件取「或」，再與「這份清單開了多選」取「且」；讀不到的一律當 false。</summary>
    private sealed class RowCheckVisibility : IMultiValueConverter
    {
        public static readonly RowCheckVisibility Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 0 || values[0] is not true) return false;
            for (var index = 1; index < values.Length; index++)
                if (values[index] is true) return true;
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
