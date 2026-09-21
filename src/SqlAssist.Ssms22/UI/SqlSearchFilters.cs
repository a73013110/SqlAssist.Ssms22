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
