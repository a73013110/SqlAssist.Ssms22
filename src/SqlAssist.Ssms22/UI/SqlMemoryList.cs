using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>History／Favorites 共用的清單與鍵盤路徑；開啟是明確動作，不是選取副作用。</summary>
internal sealed class SqlMemoryList : ListBox
{
    public event EventHandler? OpenRequested;
    public event Action<string>? RowActionRequested;

    public SqlMemoryList()
    {
        ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle();
        ItemTemplate = SqlAssistChrome.CreateSqlSummaryTemplate();
        BorderThickness = new Thickness(0);
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(this, true);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { Tag: string action } button) return;
            if (ContainerFromElement(this, button) is not ListBoxItem item) return;
            SelectedItem = ItemContainerGenerator.ItemFromContainer(item);
            e.Handled = true; RowActionRequested?.Invoke(action);
        }));
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
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
        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None && IsRowContent(e.OriginalSource))
        { e.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); }
        base.OnPreviewKeyDown(e);
    }

    private bool IsRowContent(object source)
    {
        if (source is not DependencyObject element || ContainerFromElement(this, element) is not ListBoxItem) return false;
        for (var current = element; current != null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is ButtonBase) return false;
            if (current is ListBoxItem) return true;
        }
        return false;
    }
}
