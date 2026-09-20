using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlAssist.Core.Search;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 比對位置的三段開關；常駐在工具列上，對應 <see cref="SearchQuery.Targets"/>。
/// </summary>
/// <remarks>
/// 做成分段開關而不是下拉：這是使用者切換最頻繁的一項，藏進下拉會讓每一次切換多兩次點擊。
/// 三段可以同時亮，因為 <see cref="SearchTargets"/> 本來就是旗標；<b>但不能全部關掉</b>——
/// 一個部位都不掃的查詢找不到任何東西，而畫面上與「這個字串不存在」一模一樣。
/// 最後一段按下去時維持原樣，不送出變更。
/// </remarks>
internal sealed class SqlSearchSegments : Border
{
    private readonly List<(SearchMatchTarget Target, ToggleButton Button)> _segments = new();
    private SearchTargets _value = SearchTargets.All;
    private bool _updating;

    public SqlSearchSegments()
    {
        SetResourceReference(BackgroundProperty, ThemeBrush.SegmentTrack);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;

        var track = new StackPanel { Orientation = Orientation.Horizontal };
        Child = track;

        foreach (var target in SqlSearchTargets.Order)
        {
            var label = SqlSearchTargets.LabelFor(target);
            var segment = new ToggleButton
            {
                Content = SqlAssistChrome.CreateButtonText(label),
                Style = SqlAssistChrome.CreateSegmentToggleStyle(),
                IsChecked = true,
                ToolTip = label + "：" + SqlSearchTargets.DescriptionFor(target)
            };
            AutomationProperties.SetName(segment, "比對位置：" + label);
            var flag = target.ToFlag();
            segment.Checked += (_, _) => Toggle(flag, on: true);
            segment.Unchecked += (_, _) => Toggle(flag, on: false);
            _segments.Add((target, segment));
            track.Children.Add(segment);
        }

        AutomationProperties.SetName(this, "比對位置");
    }

    public event EventHandler? ValueChanged;

    /// <summary>目前亮著的幾段；永遠至少一段。</summary>
    public SearchTargets Value
    {
        get => _value;
        set
        {
            if (value == SearchTargets.None || (value & ~SearchTargets.All) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "至少要亮一段，也不接受認不得的位元。");
            }

            if (value == _value) return;
            _value = value;
            Refresh();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Toggle(SearchTargets flag, bool on)
    {
        if (_updating) return;

        var next = on ? _value | flag : _value & ~flag;
        if (next == _value) return;

        // 最後一段關不掉。按鈕已經彈起來了，所以要把它按回去——不還原的話，畫面上三段全暗，
        // 而實際上仍在比對那一段。
        if (next == SearchTargets.None)
        {
            Refresh();
            return;
        }

        _value = next;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            foreach (var (target, button) in _segments) button.IsChecked = (_value & target.ToFlag()) != 0;
        }
        finally
        {
            _updating = false;
        }
    }
}

/// <summary>
/// 已選條件的 chip 列；預設狀態整列收起，不佔那一列。
/// </summary>
/// <remarks>
/// 這是這個版面空間極大化的關鍵：沒有條件就不留空白列。
/// 收起用 <see cref="Visibility.Collapsed"/> 而不是把高度設成 0——後者仍會參與量測，
/// 而清單少掉的正是那幾個 DIP。
///
/// <b>永遠只有一列</b>：chip 一維度一顆（見 <c>SqlSearchFilterChip</c>），而放不下時橫向捲動，
/// 不換行。換行的那一版在停靠面板裡會長到三列，而那三列換算成少看六筆結果；
/// 捲動的規矩與預覽資訊列同一份 <see cref="SqlAssistChrome.CreateHorizontalStrip"/>。
/// </remarks>
internal sealed class SqlSearchChipBar : ContentControl
{
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };

    public SqlSearchChipBar()
    {
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 4, 0, 0);
        Focusable = false;
        Content = SqlAssistChrome.CreateHorizontalStrip(_strip, "已選條件（可水平捲動）");
        AutomationProperties.SetName(this, "已選條件");
    }

    /// <summary>按下某一顆 chip 的十字；宿主據此清掉它代表的整個維度。</summary>
    public event Action<object>? RemoveRequested;

    /// <summary>按下 chip 本體；宿主據此打開那個維度的過濾面板。</summary>
    public event Action<object>? OpenRequested;

    /// <summary>換一整列 chip；空的就整列收起。</summary>
    /// <param name="canOpen">這顆 chip 的本體按得下去（有自己的面板）；null 表示都不能按。</param>
    public void SetChips<T>(IReadOnlyList<T> chips, Func<T, string> label, Func<T, bool>? canOpen = null) where T : class
    {
        if (chips is null) throw new ArgumentNullException(nameof(chips));
        if (label is null) throw new ArgumentNullException(nameof(label));

        _strip.Children.Clear();

        foreach (var chip in chips)
        {
            var text = label(chip);
            var open = canOpen?.Invoke(chip) == true;
            var element = SqlAssistChrome.CreateFilterChip(
                text, out var remove, out var openButton, open ? "：開啟面板調整" : null);
            remove.Click += (_, _) => RemoveRequested?.Invoke(chip);
            if (openButton is not null) openButton.Click += (_, _) => OpenRequested?.Invoke(chip);
            _strip.Children.Add(element);
        }

        Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
