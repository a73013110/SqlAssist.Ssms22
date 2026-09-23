using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using SqlAssist.Core.Matching;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 搜尋框裡的比對修飾開關（大小寫相同、整個字）；SQL Search 與 SQL Memory 共用一份。
/// </summary>
/// <remarks>
/// 開關照 <see cref="Definitions"/> 一份清單畫出來，值是一個 <see cref="TextMatchOptions"/>：
/// 之後多一個修飾（例如 regex）就是清單多一列，宿主、模型與記住的格式都不必再各接一顆。
/// 兩個工具窗各自宣告兩顆的那一版，圖示、名稱與說明遲早會說得不一樣，
/// 而使用者在兩處看到的是同一件事。
///
/// 寫回 <see cref="Options"/> 時不發 <see cref="Changed"/>：那是還原或同步，不是使用者按的，
/// 發出去的話宿主會再重跑一輪、再寫回來一次。
/// </remarks>
internal sealed class SqlMatchToggles
{
    // 說明都說「勾起來會少掉什麼」，不說詞界、ordinal 這些只有寫程式的人讀得懂的字：
    // 使用者要判斷的是「我現在找不到那一筆，是不是被這一顆擋掉了」。
    private static readonly (TextMatchOptions Flag, SqlIcon Icon, string Label, string ToolTip)[] Definitions =
    {
        (TextMatchOptions.MatchCasing, SqlIcon.MatchCase, "大小寫相同", "大小寫要完全一樣：搜 finish 就不會找到 Finish。"),
        (TextMatchOptions.WholeWord, SqlIcon.WholeWord, "整個字", "只找完整的字：搜 Copy 就不會找到 CopyNo 裡的那一段。"),
    };

    private readonly ToggleButton[] _buttons;
    private bool _syncing;

    public SqlMatchToggles()
    {
        _buttons = Definitions
            .Select(definition => SqlAssistChrome.CreateSearchToggle(definition.Icon, definition.Label, definition.ToolTip))
            .ToArray();

        foreach (var button in _buttons)
        {
            button.Checked += OnToggled;
            button.Unchecked += OnToggled;
        }
    }

    /// <summary>使用者按了其中一顆；寫回 <see cref="Options"/> 不算。</summary>
    public event EventHandler? Changed;

    /// <summary>依清單順序由左往右，交給 <see cref="SqlAssistChrome.CreateInputBar"/>。</summary>
    public IReadOnlyList<FrameworkElement> Buttons => _buttons;

    public TextMatchOptions Options
    {
        get
        {
            var options = TextMatchOptions.None;
            for (var index = 0; index < _buttons.Length; index++)
            {
                if (_buttons[index].IsChecked == true) options |= Definitions[index].Flag;
            }

            return options;
        }
        set
        {
            _syncing = true;
            try
            {
                for (var index = 0; index < _buttons.Length; index++)
                {
                    _buttons[index].IsChecked = (value & Definitions[index].Flag) != 0;
                }
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (!_syncing) Changed?.Invoke(this, EventArgs.Empty);
    }
}
