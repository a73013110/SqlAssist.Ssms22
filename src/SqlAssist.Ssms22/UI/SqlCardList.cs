using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 卡片清單共用的鍵盤、滑鼠與續頁路徑；開啟是明確動作，不是選取副作用。
/// </summary>
/// <remarks>
/// 列上的操作按鈕以 <typeparamref name="TAction"/> 為 Tag，點下時先選取該列再發出請求；
/// 不拿圖示或文字當識別，History 卡片與收藏版本時間軸各自的操作列舉走同一條路。
///
/// SQL Memory 與 SQL Search 的清單都繼承它。放在中性的檔案而不是 <c>SqlMemoryList.cs</c>，
/// 理由與 <c>SqlAssistChrome.Rows.cs</c> 相同：檔名說「這是 Memory 的」，下一個清單就會
/// 再寫一份虛擬化、續頁與鍵盤路徑，而那四條規則錯一條都只在捲到底或只用鍵盤時才看得出來。
/// </remarks>
internal abstract class SqlCardListBase<TAction> : ListBox where TAction : struct, Enum
{
    private Size _viewportSize = Size.Empty;
    public event EventHandler? OpenRequested;
    public event Action<TAction>? RowActionRequested;
    public event EventHandler? LoadMoreRequested;
    public bool CanAutoLoadMore { get; set; }

    public void SetRowsSource(IEnumerable rows, UIElement footer)
    {
        // 頁尾是同一個虛擬清單的最後一項，不在外面再包 ScrollViewer 破壞 recycling。
        ItemsSource = new CompositeCollection { new CollectionContainer { Collection = rows }, new SqlCardListFooter(footer) };
    }

    protected SqlCardListBase()
    {
        BorderThickness = new Thickness(0);
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(this, true);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        // 列的可用寬度就是清單的寬度：量一次，底下每一列跟著換寬度模式。
        SqlRowLayout.Track(this);
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not ScrollViewer scroll) return;
            // 邏輯捲動的 ViewportHeight 是列數，短頁尾進場也會改變；用實際 DIP 尺寸辨識視窗縮放。
            var size = new Size(scroll.ActualWidth, scroll.ActualHeight);
            var sameViewport = _viewportSize == size; _viewportSize = size;
            if (sameViewport && e.VerticalChange > 0 && e.ExtentHeightChange == 0 &&
                scroll.ScrollableHeight - scroll.VerticalOffset <= 1) RequestMore();
        }));
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { Tag: TAction action } button) return;
            if (ContainerFromElement(this, button) is not ListBoxItem item) return;
            SelectedItem = ItemContainerGenerator.ItemFromContainer(item);
            e.Handled = true; RequestAction(action);
        }));
    }

    protected void RequestAction(TAction action) => RowActionRequested?.Invoke(action);

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // 已到底端時不會再有 ScrollChanged；仍接受使用者下一次向下捲動。
        if (e.Delta < 0 && SqlAssistChrome.FindScrollViewer(this) is { } scroll && scroll.ScrollableHeight - scroll.VerticalOffset <= 1)
            RequestMore();
        base.OnPreviewMouseWheel(e);
    }

    private void RequestMore()
    {
        if (CanAutoLoadMore) LoadMoreRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (ContainerFromElement(this, e.OriginalSource as DependencyObject) is SqlCardListFooter)
        { e.Handled = true; return; }
        if (ContainerFromElement(this, e.OriginalSource as DependencyObject) is ListBoxItem item)
            item.IsSelected = true;
        base.OnPreviewMouseRightButtonDown(e);
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.ChangedButton == MouseButton.Left && IsRowContent(e.OriginalSource))
        { e.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // ↑／↓ 保留 ListBox 原生 selection/navigation；按鈕的 Enter 由 Button 自己處理。
        if (!e.Handled && e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None && IsRowContent(e.OriginalSource))
        { e.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); }
        base.OnPreviewKeyDown(e);
    }

    protected bool IsRowContent(object source)
    {
        if (source is not DependencyObject element || ContainerFromElement(this, element) is not ListBoxItem container ||
            container is SqlCardListFooter) return false;
        for (var current = element; current != null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ButtonBase) return false;
            if (current is ListBoxItem) return true;
        }
        return false;
    }
}

/// <summary>頁尾保留按鈕鍵盤操作，但不參與 SQL 選取、雙擊或 Enter 開啟。</summary>
internal sealed class SqlCardListFooter : ListBoxItem
{
    public SqlCardListFooter(UIElement content)
    {
        Content = content; Focusable = false; IsTabStop = false;
        // 自帶容器不套卡片樣板，並讓 Content 的按鈕保持原生焦點路徑。
        Template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { }
}
