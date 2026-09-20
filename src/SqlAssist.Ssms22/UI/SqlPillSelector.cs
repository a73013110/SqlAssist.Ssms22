using System;
using System.Collections.Generic;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SqlAssist.Ssms22.UI;

internal sealed class SqlPillSelector : WrapPanel
{
    private readonly List<RadioButton> _buttons = new();
    private int _selectedIndex = -1;
    public event EventHandler? SelectionChanged;

    public SqlPillSelector(params (string Label, SqlIcon Icon)[] options)
        : this(Array.ConvertAll(options, option => (option.Label, (SqlIcon?)option.Icon)))
    {
    }

    /// <summary>
    /// 只有文字的膠囊。
    /// </summary>
    /// <remarks>
    /// 給選項由資料決定、沒有固定語意圖示的過濾列用（搜尋的分類 pill 由 provider 宣告的分類產生）。
    /// 硬挑一顆看似合理的圖示套給每一個分類，會讓不同意思的選項共用同一個形狀，
    /// 而辨識本來就該同時靠形狀與文字——兩者只剩文字時，至少沒有一個錯的形狀。
    /// </remarks>
    public SqlPillSelector(params string[] labels)
        : this(Array.ConvertAll(labels, label => (label, (SqlIcon?)null)))
    {
    }

    private SqlPillSelector((string Label, SqlIcon? Icon)[] options)
    {
        var group = Guid.NewGuid().ToString("N");
        foreach (var (label, icon) in options)
        {
            var index = _buttons.Count;
            var content = icon is { } glyph
                ? (object)SqlAssistChrome.CreateIconLabel(glyph, label)
                : SqlAssistChrome.CreateButtonText(label);
            var button = new RadioButton { Content = content, GroupName = group, Style = SqlAssistChrome.CreateMemoryPillStyle() };
            AutomationProperties.SetName(button, label);
            button.Checked += (_, _) => SelectedIndex = index;
            _buttons.Add(button); Children.Add(button);
        }
        if (_buttons.Count > 0) SelectedIndex = 0;
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value == _selectedIndex) return;
            if (value < 0 || value >= _buttons.Count) throw new ArgumentOutOfRangeException(nameof(value));
            _selectedIndex = value;
            _buttons[value].IsChecked = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
