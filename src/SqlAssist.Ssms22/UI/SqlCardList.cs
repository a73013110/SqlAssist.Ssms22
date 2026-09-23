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
    private ISqlCardSelection? _selection;
    public event EventHandler? OpenRequested;
    public event Action<TAction>? RowActionRequested;
    public event EventHandler? LoadMoreRequested;
    public bool CanAutoLoadMore { get; set; }

    /// <summary>多選狀態；沒有呼叫 <see cref="EnableSelection"/> 的清單是 null，行為與沒有多選時完全相同。</summary>
    public ISqlCardSelection? Selection => _selection;

    /// <summary>
    /// 滑鼠點擊時讀修飾鍵的來源。
    /// </summary>
    /// <remarks>
    /// 按鍵事件讀 <see cref="KeyEventArgs.KeyboardDevice"/>，滑鼠事件沒有這一份，只能問鍵盤；
    /// 測試換掉它，Shift／Ctrl+點擊才不受實體鍵盤的狀態左右。
    /// </remarks>
    internal Func<ModifierKeys> ModifierSource { get; set; } = () => Keyboard.Modifiers;

    /// <summary>
    /// 開啟多選：勾選框、Ctrl／Shift+點擊、空白鍵、Ctrl+A、Esc 與動作快捷鍵。
    /// </summary>
    /// <remarks>
    /// 做成可選能力而不是另一個子類別：SQL Memory 與 SQL Search 走同一條輸入路徑，差別只在
    /// 列有沒有實作 <see cref="ISqlCheckableRow"/> 與樣板有沒有勾選欄。
    /// 勾選與 <see cref="Selector.SelectedItem"/> 分開：後者仍只代表焦點與預覽。
    /// </remarks>
    public void EnableSelection(ISqlCardSelection selection)
    {
        if (_selection is not null) throw new InvalidOperationException("這份清單已經開了多選。");
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        SqlRowCheck.SetIsAvailable(this, true);
        selection.Changed += (_, _) => SqlRowCheck.SetIsActive(this, selection.IsActive);
        AddHandler(SqlRowCheckBox.ToggleRequestedEvent, new RoutedEventHandler((_, e) =>
        {
            e.Handled = true;
            if (ContainerFromElement(this, e.OriginalSource as DependencyObject) is not ListBoxItem container ||
                container is SqlCardListFooter) return;
            var item = ItemContainerGenerator.ItemFromContainer(container);
            if (ModifierSource() == ModifierKeys.Shift) selection.SelectRange(item, SelectedItem);
            else selection.Toggle(item);
        }));
    }

    /// <summary>把鍵盤焦點放回目前的焦點列；選取工具列收起時焦點不能掉到沒有人接的地方。</summary>
    public void FocusCurrentRow()
    {
        if (SelectedItem is { } item && ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
        else Focus();
    }

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

    /// <summary>
    /// 多選的滑鼠路徑：Ctrl+點擊切換、Shift+點擊選一段；多選模式中單擊就是切換。
    /// </summary>
    /// <remarks>
    /// 自己處理而不交給 <see cref="ListBox"/>：單選模式下 Ctrl+點擊已選取的那一列會把它取消選取，
    /// 焦點與預覽就跟著清空。這裡改成先把焦點列換到按下去的那一列（預覽跟著走），再改勾選。
    /// 雙擊的第二下不再切換，第一下已經切過了；雙擊本身照樣開新 Query。
    /// </remarks>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_selection is { } selection && e.ClickCount <= 1 && IsRowContent(e.OriginalSource) &&
            ContainerFromElement(this, (DependencyObject)e.OriginalSource) is ListBoxItem container)
        {
            var modifiers = ModifierSource();
            var toggle = modifiers == ModifierKeys.Control || (modifiers == ModifierKeys.None && selection.IsActive);
            if (toggle || modifiers == ModifierKeys.Shift)
            {
                e.Handled = true;
                var item = ItemContainerGenerator.ItemFromContainer(container);
                var anchor = SelectedItem;
                SelectedItem = item;
                container.Focus();
                if (toggle) selection.Toggle(item);
                else selection.SelectRange(item, anchor);
            }
        }

        base.OnPreviewMouseLeftButtonDown(e);
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
        if (!e.Handled && _selection is { } selection && HandleSelectionKey(selection, e)) e.Handled = true;
        // ↑／↓ 保留 ListBox 原生 selection/navigation；按鈕的 Enter 由 Button 自己處理。
        if (!e.Handled && e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None && IsRowContent(e.OriginalSource))
        { e.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// 多選的鍵盤路徑。
    /// </summary>
    /// <remarks>
    /// 空白鍵作用在焦點列（Shift 選到焦點列為止）；Ctrl+A 與工具列的全選同一件事；Esc 離開多選模式。
    /// 動作的快捷鍵只在多選模式中接，所以 Ctrl+C 平常仍是原本那一條路。
    /// ↑／↓ 不碰：多選模式中方向鍵仍只移動焦點，預覽跟著焦點走。
    /// </remarks>
    private bool HandleSelectionKey(ISqlCardSelection selection, KeyEventArgs e)
    {
        var modifiers = e.KeyboardDevice.Modifiers;
        switch (e.Key)
        {
            case Key.Space when modifiers is ModifierKeys.None or ModifierKeys.Shift && IsRowContent(e.OriginalSource) &&
                                ContainerFromElement(this, (DependencyObject)e.OriginalSource) is ListBoxItem container:
                var item = ItemContainerGenerator.ItemFromContainer(container);
                if (modifiers == ModifierKeys.Shift) selection.SelectRange(item, SelectedItem);
                else selection.Toggle(item);
                return true;
            case Key.A when modifiers == ModifierKeys.Control:
                if (selection.LoadedCount == 0) return false;
                selection.SelectAll();
                return true;
            case Key.Escape when modifiers == ModifierKeys.None && selection.IsActive:
                selection.Clear();
                return true;
            default:
                return selection.TryInvokeShortcut(e.Key, modifiers);
        }
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
