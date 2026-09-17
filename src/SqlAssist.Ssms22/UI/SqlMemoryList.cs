using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections;
using System.Windows.Data;

namespace SqlAssist.Ssms22.UI;

/// <summary>卡片、快捷選單與 Preview 共用的列操作；按鈕以它為 Tag，不拿圖示名稱當動作識別。</summary>
internal enum SqlMemoryRowAction { Copy, Open, AddFavorite, Edit, Revisions, Delete }

/// <summary>操作適用於哪一種列；卡片模板與快捷選單都依它隱藏，不用停用的灰色按鈕佔位。</summary>
internal enum SqlMemoryRowKind { Any, History, Favorite }

/// <summary>
/// 一個列操作的外觀與適用範圍。卡片、快捷選單與 Preview 都從 <see cref="SqlMemoryRowCommand.All"/> 建立，
/// 新增操作只加一筆，三處不會各自漏掉或順序不一。
/// </summary>
/// <remarks>
/// <see cref="All"/> 的順序就是三處的呈現順序，動線固定為「主要動作 → 一般安全操作 → 收藏管理 → 破壞性操作」；
/// 呼叫端只能整段跳過不適用的項目，不得自己重排，否則同一批按鈕在卡片與 Preview 又會對不起來。
/// </remarks>
internal sealed class SqlMemoryRowCommand
{
    private SqlMemoryRowCommand(SqlMemoryRowAction action, SqlIcon icon, string label, SqlMemoryRowKind kind,
        string? labelProperty = null, bool separated = false, SqlActionTone tone = SqlActionTone.Neutral)
    {
        Action = action; Icon = icon; Label = label; Kind = kind;
        LabelProperty = labelProperty; IsSeparated = separated; Tone = tone;
    }

    public static IReadOnlyList<SqlMemoryRowCommand> All { get; } = new[]
    {
        new SqlMemoryRowCommand(SqlMemoryRowAction.Open, SqlIcon.Open, "在新 Query 開啟（不執行）", SqlMemoryRowKind.Any),
        new SqlMemoryRowCommand(SqlMemoryRowAction.Copy, SqlIcon.Copy, "複製 SQL", SqlMemoryRowKind.Any),
        new SqlMemoryRowCommand(SqlMemoryRowAction.AddFavorite, SqlIcon.Favorite, "新增至收藏", SqlMemoryRowKind.History,
            tone: SqlActionTone.Favorite),
        // 名稱、標註與 SQL 在同一個編輯器一次儲存，不拆成兩個各自做版本檢查的對話框。
        new SqlMemoryRowCommand(SqlMemoryRowAction.Edit, SqlIcon.Edit, "編輯收藏", SqlMemoryRowKind.Favorite),
        new SqlMemoryRowCommand(SqlMemoryRowAction.Revisions, SqlIcon.History, "版本歷史", SqlMemoryRowKind.Favorite),
        // 破壞性操作與其他操作隔開，並一律經確認；標籤依列種類說清楚刪的是紀錄還是收藏。
        new SqlMemoryRowCommand(SqlMemoryRowAction.Delete, SqlIcon.Remove, "刪除", SqlMemoryRowKind.Any,
            labelProperty: "DeleteLabel", separated: true, tone: SqlActionTone.Danger),
    };

    public SqlMemoryRowAction Action { get; }
    public SqlIcon Icon { get; }

    /// <summary>靜態標籤；<see cref="LabelProperty"/> 存在時，繫結到列上依狀態變化的說明。</summary>
    public string Label { get; }

    public SqlMemoryRowKind Kind { get; }
    public string? LabelProperty { get; }

    /// <summary>與前一組操作之間留分隔；快捷選單畫分隔線，卡片留較寬的間距。</summary>
    public bool IsSeparated { get; }

    /// <summary>停駐與按下的語意色；只有需要警示或明確歸類的操作離開中性色，其餘沿用選取色。</summary>
    public SqlActionTone Tone { get; }

    public bool AppliesTo(bool favorite) =>
        Kind == SqlMemoryRowKind.Any || (Kind == SqlMemoryRowKind.Favorite) == favorite;

    public static SqlMemoryRowCommand For(SqlMemoryRowAction action)
    {
        foreach (var command in All) if (command.Action == action) return command;
        throw new ArgumentOutOfRangeException(nameof(action), action, "沒有這個列操作。");
    }
}

/// <summary>History／Favorites 共用的清單；卡片樣板與 Delete 鍵是這份清單自己的，其餘路徑在基底。</summary>
internal sealed class SqlMemoryList : SqlMemoryListBase<SqlMemoryRowAction>
{
    public SqlMemoryList()
    {
        ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle();
        ItemTemplate = SqlAssistChrome.CreateSqlSummaryTemplate();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Delete 與檔案總管相同：只對聚焦的卡片發出請求，是否刪除仍由確認框決定。
        if (e.Key == Key.Delete && e.KeyboardDevice.Modifiers == ModifierKeys.None && IsRowContent(e.OriginalSource))
        { e.Handled = true; RequestAction(SqlMemoryRowAction.Delete); }
        base.OnPreviewKeyDown(e);
    }
}

/// <summary>
/// SQL Memory 各種清單共用的鍵盤、滑鼠與續頁路徑；開啟是明確動作，不是選取副作用。
/// </summary>
/// <remarks>
/// 列上的操作按鈕以 <typeparamref name="TAction"/> 為 Tag，點下時先選取該列再發出請求；
/// 不拿圖示或文字當識別，History 卡片與收藏版本時間軸各自的操作列舉走同一條路。
/// </remarks>
internal abstract class SqlMemoryListBase<TAction> : ListBox where TAction : struct, Enum
{
    private Size _viewportSize = Size.Empty;
    public event EventHandler? OpenRequested;
    public event Action<TAction>? RowActionRequested;
    public event EventHandler? LoadMoreRequested;
    public bool CanAutoLoadMore { get; set; }

    public void SetRowsSource(IEnumerable rows, UIElement footer)
    {
        // 頁尾是同一個虛擬清單的最後一項，不在外面再包 ScrollViewer 破壞 recycling。
        ItemsSource = new CompositeCollection { new CollectionContainer { Collection = rows }, new SqlMemoryListFooter(footer) };
    }

    protected SqlMemoryListBase()
    {
        BorderThickness = new Thickness(0);
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(this, true);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
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
        if (e.Delta < 0 && FindScrollViewer(this) is { } scroll && scroll.ScrollableHeight - scroll.VerticalOffset <= 1)
            RequestMore();
        base.OnPreviewMouseWheel(e);
    }

    private void RequestMore()
    {
        if (CanAutoLoadMore) LoadMoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (ContainerFromElement(this, e.OriginalSource as DependencyObject) is SqlMemoryListFooter)
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
            container is SqlMemoryListFooter) return false;
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
internal sealed class SqlMemoryListFooter : ListBoxItem
{
    public SqlMemoryListFooter(UIElement content)
    {
        Content = content; Focusable = false; IsTabStop = false;
        // 自帶容器不套卡片樣板，並讓 Content 的按鈕保持原生焦點路徑。
        Template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { }
}
