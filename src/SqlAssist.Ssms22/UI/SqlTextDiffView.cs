using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 行級差異的唯讀呈現；比對本身在 Core 的 <see cref="SqlTextDiff"/>，這裡只負責畫與捲動。
/// </summary>
/// <remarks>
/// 列表是 recycling 虛擬化，十萬行的整段取代也只建立看得到的列。捲軸屬於編輯區內容，沿用原生樣式。
/// 換一份結果時以共用的 120 ms 淡入出現，不縮放不位移；動畫關閉時直接顯示。
/// </remarks>
internal sealed class SqlTextDiffView : UserControl
{
    private readonly ListBox _lines = new()
    {
        BorderThickness = new Thickness(0),
        FontFamily = SqlAssistChrome.CodeFont,
        FontSize = SqlAssistChrome.DefaultMetrics.Body,
        Padding = new Thickness(0, 4, 0, 4),
        ItemContainerStyle = SqlAssistChrome.CreatePlainItemStyle(),
        ItemTemplate = SqlAssistChrome.CreateDiffLineTemplate()
    };

    public SqlTextDiffView()
    {
        _lines.SetResourceReference(BackgroundProperty, ThemeBrush.ListBackground);
        ScrollViewer.SetHorizontalScrollBarVisibility(_lines, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(_lines, true);
        VirtualizingPanel.SetIsVirtualizing(_lines, true);
        VirtualizingPanel.SetVirtualizationMode(_lines, VirtualizationMode.Recycling);
        AutomationProperties.SetName(_lines, "SQL 差異");
        Content = _lines;
    }

    private int? _pendingOffset;

    public SqlTextDiffResult? Result { get; private set; }

    /// <summary>顯示一份結果，並把第一處變更捲到可見範圍上緣附近（保留三行脈絡）。</summary>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public void Show(SqlTextDiffResult result, bool? motion = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        _lines.ItemsSource = result.Lines;
        // 捲動區要等版面建立；只在有待捲動時掛上 LayoutUpdated，捲完就解除，平時不佔版面事件。
        if (_pendingOffset is null) _lines.LayoutUpdated += OnLayoutUpdated;
        _pendingOffset = Math.Max(0, result.FirstChange - 3);
        ApplyPendingScroll();
        SqlAssistChrome.PlayAppear(_lines, motion);
    }

    public void Clear()
    {
        Result = null; _pendingOffset = null;
        _lines.LayoutUpdated -= OnLayoutUpdated;
        _lines.ItemsSource = null;
    }

    /// <summary>邏輯捲動的位移單位是列，不必先實體化目標列。</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e) => ApplyPendingScroll();

    private void ApplyPendingScroll()
    {
        if (_pendingOffset is not { } offset || !_lines.IsVisible) return;
        if (FindScrollViewer(_lines) is not { } scroll || (scroll.ExtentHeight <= 0 && Result?.Lines.Count > 0)) return;
        _pendingOffset = null;
        _lines.LayoutUpdated -= OnLayoutUpdated;
        scroll.ScrollToHorizontalOffset(0);
        scroll.ScrollToVerticalOffset(offset);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
