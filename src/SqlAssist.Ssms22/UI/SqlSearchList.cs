using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 搜尋結果清單：名稱命中與本文命中分兩組，容器仍然虛擬化。
/// </summary>
/// <remarks>
/// 分組走 <see cref="CollectionViewSource"/> 而不是在集合裡塞標頭列：塞標頭的話，每一列都要
/// 先問「你是不是標頭」，而選取、鍵盤導覽與樣板選擇三處都會各漏一次。代價是必須明確打開
/// <see cref="VirtualizingPanel.IsVirtualizingWhenGroupingProperty"/>——預設是關的，
/// 分組之後整份清單會一次具體化，而這裡一輪可以有兩百列。
///
/// 開啟是明確動作（雙擊或 Enter），不是選取的副作用；選取只換預覽。與 SQL Memory 的清單
/// 同一條規則，理由也相同：選取會被方向鍵連續觸發。
/// </remarks>
internal sealed class SqlSearchList : ListBox
{
    private readonly CollectionViewSource _view = new();

    public SqlSearchList()
    {
        BorderThickness = new Thickness(0);
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle(removable: false);
        ItemTemplate = SqlAssistChrome.CreateSearchHitTemplate();
        GroupStyle.Add(SqlAssistChrome.CreateSearchGroupStyle());
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(this, true);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(this, true);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
    }

    /// <summary>雙擊或卡片上的 Enter；選取本身不觸發。</summary>
    public event EventHandler? OpenRequested;

    /// <summary>
    /// 綁上結果集合，並依 <paramref name="groupBy"/> 分組。
    /// </summary>
    /// <remarks>
    /// 只做一次：之後改的是集合內容，不是繫結。每次結果都重建一個
    /// <see cref="CollectionViewSource"/> 的話，捲動位置與選取會跟著整份換掉。
    /// </remarks>
    public void SetRowsSource(IEnumerable rows, string groupBy)
    {
        _view.Source = rows ?? throw new ArgumentNullException(nameof(rows));
        _view.GroupDescriptions.Clear();
        _view.GroupDescriptions.Add(new PropertyGroupDescription(groupBy));
        ItemsSource = _view.View;
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.ChangedButton == MouseButton.Left && IsRowContent(e.OriginalSource))
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // ↑／↓ 保留 ListBox 原生的選取與導覽；只有 Enter 是「開啟」。
        if (!e.Handled && e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            IsRowContent(e.OriginalSource))
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (ContainerFromElement(this, e.OriginalSource as DependencyObject) is ListBoxItem item) item.IsSelected = true;
        base.OnPreviewMouseRightButtonDown(e);
    }

    private bool IsRowContent(object source) =>
        source is DependencyObject element && ContainerFromElement(this, element) is ListBoxItem;
}
