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
    /// <summary>過濾面板裡那條橫線；把第一列那個預設與底下的複選區切開。</summary>
    /// <remarks>
    /// 與篩選列上那兩條是不同的東西：那兩條是直的，分的是工具列上一顆顆篩選按鈕
    /// （見 <see cref="CreateGroupDivider"/>）；這一條是橫的，分的是面板裡「預設值」與
    /// 「自己挑這些」兩區。沒有它的那一版，第一列畫成 radio 會讓人以為整份清單只能選一個。
    /// 淡度沿用群內那一級：它分的是同一個面板裡的兩區，不是兩個問題。
    /// </remarks>
    public static Border CreateFilterPanelDivider()
    {
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Opacity = ItemDividerOpacity,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Focusable = false
        };
        divider.SetResourceReference(Border.BackgroundProperty, ThemeBrush.Hairline);
        return divider;
    }

    /// <summary>面板第一列那個預設；一律畫成 radio，內容由呼叫端塞一個 <see cref="SqlFilterRow"/>。</summary>
    /// <remarks>
    /// 它與底下每一個選項<b>互斥</b>——勾任何一個名稱它就退勾，選它就把整個維度清空——所以形狀
    /// 要是 radio。核取方塊的合約是可勾可取消，而這一列取消不掉（「一個都不選」就是它自己），
    /// 畫成核取方塊讀起來像壞掉。複選面板也用 radio，理由同上；互斥仍由模型負責，
    /// <see cref="SqlFilterRow.GroupName"/> 每一列各一個，WPF 的自動互斥照樣關著。
    ///
    /// 它不進 <see cref="CreateFilterOptionList"/> 那份虛擬化清單：它是這個維度的預設值與
    /// 目前狀態，捲得走的那一版在名稱上百個時把狀態藏起來，而它又是回得去的那一條路。
    /// 留在清單外面也讓它天生不受搜尋框過濾，不必再特判一次。
    /// </remarks>
    public static ContentPresenter CreateFilterDefaultRow() =>
        new() { ContentTemplate = CreateFilterOptionRow<RadioButton>(CreateRadioTemplate()) };

    /// <summary>
    /// 過濾下拉按鈕的樣板：幽靈按鈕，有條件時（<see cref="SqlFilterFlyout.IsNarrowed"/>）換成強調底加強調框。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="CreateToggleStyle"/> 的「開著」同一組色。宣告在停駐、焦點與按下<b>前面</b>：
    /// 這是一顆會開面板的按鈕，停駐與按下的回饋要照常出現，而停駐只換底色，強調框仍留著，
    /// 滑鼠掃過時看得出條件還在。只換筆刷，外框本來就預留 1 DIP。
    /// </remarks>
    public static ControlTemplate CreateFilterButtonTemplate()
    {
        var template = CreateGhostButtonTemplate();
        var narrowed = new Trigger { Property = SqlFilterFlyout.IsNarrowedProperty, Value = true };
        narrowed.Setters.Add(ThemeResourceSet.Setter(Border.BackgroundProperty, ThemeBrush.AccentBackground, "bg"));
        narrowed.Setters.Add(ThemeResourceSet.Setter(Border.BorderBrushProperty, ThemeBrush.AccentBorder, "bg"));
        template.Triggers.Insert(0, narrowed);
        return template;
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
