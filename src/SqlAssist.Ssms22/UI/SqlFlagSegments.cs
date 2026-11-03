using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SqlAssist.Ssms22.UI;

/// <summary>分段開關上的一段：它代表的旗標、標籤與停留說明。</summary>
internal sealed record SqlFlagSegment<TFlags>(TFlags Flag, string Label, string ToolTip) where TFlags : struct, Enum;

/// <summary>
/// 比對位置的分段開關；常駐在工具列上，每一段是一個旗標。SQL Search（名稱／內容／資料行）與
/// 資料字典（名稱／資料行）共用這一份。
/// </summary>
/// <remarks>
/// 做成分段開關而不是下拉：這是使用者切換最頻繁的一項，藏進下拉會讓每一次切換多兩次點擊。
/// 幾段可以同時亮，因為比對位置本來就是旗標；<b>但不能全部關掉</b>——
/// 一個部位都不比對的查詢找不到任何東西，而畫面上與「這個字串不存在」一模一樣。
/// 最後一段按下去時維持原樣，不送出變更。
/// </remarks>
internal sealed class SqlFlagSegments<TFlags> : Border where TFlags : struct, Enum
{
    private readonly List<(long Flag, ToggleButton Button)> _segments = new();
    private readonly long _all;
    private long _value;
    private bool _updating;

    /// <param name="name">整組的朗讀名稱；每一段的朗讀名稱是「名稱：標籤」。</param>
    [Localizable(false)]
    public SqlFlagSegments(string name, IReadOnlyList<SqlFlagSegment<TFlags>> segments)
    {
        if (segments is null || segments.Count == 0) throw new ArgumentException("至少要有一段。", nameof(segments));

        SetResourceReference(BackgroundProperty, ThemeBrush.SegmentTrack);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(2);
        VerticalAlignment = VerticalAlignment.Center;

        var track = new StackPanel { Orientation = Orientation.Horizontal };
        Child = track;

        foreach (var segment in segments)
        {
            var flag = Bits(segment.Flag);
            if (flag == 0 || (_all & flag) != 0) throw new ArgumentException("每一段要是一個不重複的非零旗標。", nameof(segments));

            var button = new ToggleButton
            {
                Content = SqlAssistChrome.CreateButtonText(segment.Label),
                Style = SqlAssistChrome.CreateSegmentToggleStyle(),
                IsChecked = true,
                ToolTip = segment.ToolTip
            };
            AutomationProperties.SetName(button, SearchControlText.TargetSegmentName(segment.Label));
            button.Checked += (_, _) => Toggle(flag, on: true);
            button.Unchecked += (_, _) => Toggle(flag, on: false);
            _segments.Add((flag, button));
            track.Children.Add(button);
            _all |= flag;
        }

        _value = _all;
        AutomationProperties.SetName(this, name);
    }

    /// <summary>只在使用者按分段時送出；程式設定 <see cref="Value"/> 是把畫面擺回去，不送。</summary>
    public event EventHandler? ValueChanged;

    /// <summary>目前亮著的幾段；永遠至少一段。</summary>
    [Localizable(false)]
    public TFlags Value
    {
        get => (TFlags)Enum.ToObject(typeof(TFlags), _value);
        set
        {
            var bits = Bits(value);
            if (bits == 0 || (bits & ~_all) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "至少要亮一段，也不接受沒有對應分段的位元。");
            }

            if (bits == _value) return;
            _value = bits;
            Refresh();
        }
    }

    private static long Bits(TFlags value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

    private void Toggle(long flag, bool on)
    {
        if (_updating) return;

        var next = on ? _value | flag : _value & ~flag;
        if (next == _value) return;

        // 最後一段關不掉。按鈕已經彈起來了，所以要把它按回去——不還原的話，畫面上每一段全暗，
        // 而實際上仍在比對那一段。
        if (next == 0)
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
            foreach (var (flag, button) in _segments) button.IsChecked = (_value & flag) != 0;
        }
        finally
        {
            _updating = false;
        }
    }
}
