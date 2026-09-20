using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 過濾面板（<see cref="SqlFilterFlyout"/>）的共用樣板。
/// </summary>
/// <remarks>
/// SQL Memory 與 SQL Search 共用同一份面板，所以它的按鈕樣式與選項清單也只有這一個來源；
/// 放在這裡而不是控制項旁邊，理由與其他功能一樣——樣式只有 <see cref="SqlAssistChrome"/>
/// 一個出處，呼叫端只組版面。
/// </remarks>
internal static partial class SqlAssistChrome
{
    /// <summary>篩選群組之間那一條分隔線的高度；比按鈕矮一截，讀起來是一條界線不是一個邊框。</summary>
    private const double FilterDividerHeight = 18d;

    /// <summary>分隔線左右各留的間距；群<b>內</b>只有 4 DIP，兩個數字的差就是「這是兩群」。</summary>
    private const double FilterDividerGap = 6d;

    /// <summary>群<b>內</b>那一條的高度；比群間矮一截，兩條並排時分得出哪一條是界線。</summary>
    private const double FilterItemDividerHeight = 10d;

    /// <summary>群內分隔線左右各留的間距；與沒有線的那一版同一個數字，加線不改版面密度。</summary>
    private const double FilterItemDividerGap = 4d;

    /// <summary>群內那一條的濃度；髮絲線本來就淡，再降一階才不會讀成群界。</summary>
    private const double FilterItemDividerOpacity = 0.55d;

    /// <summary>
    /// 工具列上兩個篩選<b>群組</b>之間的淡色分隔線。
    /// </summary>
    /// <remarks>
    /// 分群的規則只有一條：回答<b>同一個問題</b>的控制項是一群。
    /// SQL Memory 的「狀態」「期間」與連線是三群，SQL Search 第二層的「搜哪裡（伺服器、資料庫）」、
    /// 「搜什麼（種類）」與「比對哪裡（名稱／內容／欄位）」是三群。
    ///
    /// 沒有這條線時，一排按鈕看起來是同一組可以互相取代的選項，而使用者會去找一顆「全部」
    /// 把它們一起關掉；全靠加大間距分群的那一版在窄窗收完字之後就分不出來了，
    /// 因為那時候每一顆本來就只剩一個圖示。
    ///
    /// 群<b>內</b>另有一條矮一截、淡一階的 <see cref="CreateFilterItemDivider"/>：每一顆篩選
    /// 之間都看得到界線，而兩級的高度與間距差讓分群仍然讀得出來。兩條畫成同一種的那一版
    /// 等於把分群取消掉，使用者會把「種類」讀成第三個範圍條件。
    ///
    /// 線本身不表達狀態，所以用 <see cref="ThemeBrush.Hairline"/> 而不是任何語意色，
    /// 也不隨停駐或選取改變；<see cref="UIElement.SnapsToDevicePixels"/> 讓它在 150% DPI 下
    /// 仍然是實心的一條，不糊成兩條半透明的。
    /// </remarks>
    public static Border CreateFilterGroupDivider() =>
        CreateFilterDivider(FilterDividerHeight, FilterDividerGap, opacity: 1d);

    /// <summary>同一群裡兩顆篩選之間那一條；矮一截、淡一階，間距仍是群內的 4 DIP。</summary>
    /// <remarks>理由與兩級的分工見 <see cref="CreateFilterGroupDivider"/>。</remarks>
    public static Border CreateFilterItemDivider() =>
        CreateFilterDivider(FilterItemDividerHeight, FilterItemDividerGap, FilterItemDividerOpacity);

    private static Border CreateFilterDivider(double height, double gap, double opacity)
    {
        var divider = new Border
        {
            Width = 1,
            Height = height,
            Margin = new Thickness(gap, 0, gap, 0),
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Focusable = false
        };
        divider.SetResourceReference(Border.BackgroundProperty, ThemeBrush.Hairline);
        // 分隔線只是視覺上的分群，不唸出來：每一群的名稱已經說得出同一件事。
        // WPF 不會替沒有內容也不可聚焦的 Border 產生自動化節點，所以這裡不必再壓一次。
        return divider;
    }

    /// <summary>工具列上的過濾下拉按鈕；與其他工具列按鈕同高，不另立一種外觀。</summary>
    public static Style CreateFilterButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, InterfaceFont));
        style.Setters.Add(new Setter(Control.FontSizeProperty, DefaultMetrics.Body));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 26d));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(ThemeResourceSet.Setter(Control.ForegroundProperty, ThemeBrush.ListForeground));
        return style;
    }

    /// <summary>
    /// 過濾面板的選項清單：recycling 虛擬化的 <see cref="ItemsControl"/>，捲軸沿用覆蓋式。
    /// </summary>
    /// <remarks>
    /// 不用 <c>ScrollViewer</c> 疊 <c>StackPanel</c>：那個形狀在面板展開的那一刻，就把每一個
    /// 資料庫、每一個分類都建成一顆 <see cref="CheckBox"/>，而面板一次只看得到十來列。
    /// 樣板在這裡建一次給所有列共用，回收的容器換的只有 <c>DataContext</c>；快取的是
    /// <see cref="ControlTemplate"/> 與 <see cref="DataTemplate"/>，不是已經有 parent 的元素。
    ///
    /// 標題與選項攤成同一份平的清單，不做巢狀分組：分組要另外開
    /// <c>IsVirtualizingWhenGrouping</c> 才虛擬化得了，而那是一個很容易漏掉的開關。
    /// </remarks>
    /// <param name="maxHeight">面板限高；捲的是選項本身，搜尋框與命令鈕要一直看得見。</param>
    /// <param name="single">
    /// 單選：選項畫成 radio。形狀就是語意，勾選框排成一列而只有一個能生效，
    /// 使用者會勾第二個然後發現第一個自己不見了。
    /// </param>
    public static ItemsControl CreateFilterOptionList(double maxHeight, bool single = false)
    {
        var option = single
            ? CreateFilterOptionRow<RadioButton>(CreateRadioTemplate())
            : CreateFilterOptionRow<CheckBox>(CreateCheckBoxTemplate());
        var rows = new FilterOptionRowSelector(CreateFilterCaptionRow(), option);
        var list = new ItemsControl
        {
            MaxHeight = maxHeight,
            Focusable = false,
            ItemTemplateSelector = rows,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            Template = CreateFilterOptionListTemplate()
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        return list;
    }

    /// <summary>清單殼層：覆蓋式捲軸加 <see cref="ItemsPresenter"/>。</summary>
    /// <remarks>
    /// <c>CanContentScroll</c> 設在這裡而不是外面：它不是可繼承的屬性，虛擬化面板要當上
    /// <c>IScrollInfo</c> 就得由這一層的 <see cref="ScrollViewer"/> 自己開。
    /// </remarks>
    private static ControlTemplate CreateFilterOptionListTemplate()
    {
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(UIElement.FocusableProperty, false);
        scroll.SetValue(Control.TemplateProperty, CreateOverlayScrollTemplate());
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        return new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroll };
    }

    /// <summary>段落標題列；與 <see cref="CreateLabel"/> 同一種字重與色階，上緣間距由列自己帶。</summary>
    private static DataTemplate CreateFilterCaptionRow()
    {
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(SqlFilterRow.Label)));
        caption.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(SqlFilterRow.Margin)));
        caption.SetValue(TextBlock.FontFamilyProperty, InterfaceFont);
        caption.SetValue(TextBlock.FontSizeProperty, DefaultMetrics.Caption);
        caption.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        caption.SetResourceReference(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        var template = new DataTemplate(typeof(SqlFilterRow)) { VisualTree = caption };
        template.Seal();
        return template;
    }

    /// <summary>選項列；與對話框的核取方塊（或單選鈕）同一個外觀，狀態由繫結帶。</summary>
    /// <remarks>
    /// <see cref="ToggleButton.IsCheckedProperty"/> 走雙向繫結而不是 <c>Checked</c>／<c>Unchecked</c>：
    /// 回收的容器換 DataContext 時繫結會把新值推進來，那不是使用者的動作，掛事件等於替他按一次。
    ///
    /// 單選那一份把 <see cref="RadioButton.GroupNameProperty"/> 繫到列自己的一個唯一字串，
    /// 等於<b>關掉</b> WPF 依父容器自動互斥的那一套。互斥由模型負責：自動互斥會在使用者選了新的
    /// 那一個之後才去取消舊的，而取消觸發的是同一組雙向繫結，症狀是剛選好的範圍被上一個
    /// 選項的回呼清掉。虛擬化又讓它更難看——沒有實體化的那幾列根本不在群組裡。
    /// </remarks>
    private static DataTemplate CreateFilterOptionRow<T>(ControlTemplate box) where T : ToggleButton
    {
        var option = new FrameworkElementFactory(typeof(T));
        option.SetValue(Control.TemplateProperty, box);
        if (typeof(T) == typeof(RadioButton))
        {
            option.SetBinding(RadioButton.GroupNameProperty, new Binding(nameof(SqlFilterRow.GroupName)));
        }

        option.SetValue(Control.FontFamilyProperty, InterfaceFont);
        option.SetValue(Control.FontSizeProperty, DefaultMetrics.Caption);
        option.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        option.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(SqlFilterRow.Margin)));
        option.SetBinding(ContentControl.ContentProperty, new Binding(nameof(SqlFilterRow.Label)));
        option.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(SqlFilterRow.ToolTip)));
        option.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(SqlFilterRow.Label)));
        option.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding(nameof(SqlFilterRow.IsSelected)) { Mode = BindingMode.TwoWay });
        option.SetResourceReference(Control.ForegroundProperty, ThemeBrush.ListForeground);
        var template = new DataTemplate(typeof(SqlFilterRow)) { VisualTree = option };
        template.Seal();
        return template;
    }

    /// <summary>兩種列共用一份平清單；回收的容器換 DataContext 時會重挑樣板。</summary>
    private sealed class FilterOptionRowSelector : DataTemplateSelector
    {
        private readonly DataTemplate _caption;
        private readonly DataTemplate _option;

        public FilterOptionRowSelector(DataTemplate caption, DataTemplate option)
        {
            _caption = caption;
            _option = option;
        }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is SqlFilterRow { IsCaption: true } ? _caption : _option;
    }
}
