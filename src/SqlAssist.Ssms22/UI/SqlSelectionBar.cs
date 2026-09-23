using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 多選模式的選取工具列：進入模式時蓋在搜尋列（貼著清單的那一層）上，高度相同。
/// </summary>
/// <remarks>
/// 蓋住而不是另起一列：多選時改搜尋字會清空勾選，把那一格讓給「✕、已選 N 筆、全選、動作」
/// 就不會誤改條件，而清單也不會因為多一列而往下跳。被蓋住的那一列停用，Tab 走不進去。
///
/// 動作按鈕照 <see cref="ISqlCardSelection.Actions"/> 畫，工具列本身不認得「複製」；
/// 之後加一個動作只是多一筆描述。全部符合時的「已載入 N 筆」與背景讀取的進度、取消都留在
/// 這一列裡，不另起一列：工具列下方再掛一條提示的那一版，每次全選都把清單往下推一列。
/// </remarks>
internal sealed class SqlSelectionBar
{
    private readonly ISqlCardSelection _selection;
    private readonly FrameworkElement _covered;
    private readonly bool? _motion;
    private readonly Border _bar;
    private readonly TextBlock _count;
    private readonly TextBlock _note;
    private readonly Button _selectAll;
    private readonly Button _cancel;
    private readonly StackPanel _actions;
    private readonly List<(SqlSelectionAction Action, Button Button, SqlIconImage Glyph)> _buttons = new();
    private readonly DispatcherTimer _done;
    private SqlIconImage? _doneGlyph;
    private SqlIcon? _doneIcon;
    private (string Text, Action Cancel)? _progress;
    private bool _shown;
    private string _countText = "";

    /// <param name="covered">被蓋住的那一列（搜尋列）；多選模式中停用並淡出。</param>
    /// <param name="motion">null 讀全域動畫設定；測試明確指定。</param>
    public SqlSelectionBar(ISqlCardSelection selection, FrameworkElement covered, bool? motion = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _covered = covered ?? throw new ArgumentNullException(nameof(covered));
        _motion = motion;

        var close = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "離開多選（Esc）");
        close.Click += (_, _) => _selection.Clear();
        _count = SqlAssistChrome.CreateSelectionCount();
        AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
        _note = SqlAssistChrome.CreateSelectionNote();
        _selectAll = SqlAssistChrome.CreateSelectionTextButton("全選", "勾選全部符合條件的項目，包括還沒載入的（Ctrl+A）");
        _selectAll.Click += (_, _) => _selection.SelectAll();
        _cancel = SqlAssistChrome.CreateSelectionTextButton("取消", "停止讀取；剪貼簿不變");
        _cancel.Visibility = Visibility.Collapsed;
        _cancel.Click += (_, _) => _progress?.Cancel();

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Left);
        content.Children.Add(close);
        _actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in selection.Actions)
        {
            var button = SqlAssistChrome.CreateSelectionActionButton(action.Icon, action.Label, action.Description, out var glyph);
            var captured = action;
            button.Click += (_, _) => _ = _selection.InvokeAsync(captured);
            _actions.Children.Add(button);
            _buttons.Add((action, button, glyph));
        }

        // 靠右那幾顆先宣告，筆數與說明最後吃剩下的寬度並省略；窄版時讓的是字，不是按鈕。
        foreach (var trailing in new FrameworkElement[] { _actions, _cancel, _selectAll })
        {
            DockPanel.SetDock(trailing, Dock.Right);
            content.Children.Add(trailing);
        }

        DockPanel.SetDock(_count, Dock.Left);
        content.Children.Add(_count);
        content.Children.Add(_note);
        _bar = SqlAssistChrome.CreateSelectionBarSurface(content);
        AutomationProperties.SetName(_bar, "多選工具列");
        _bar.PreviewKeyDown += OnBarKeyDown;

        // 被蓋住的那一列決定高度，工具列拉滿同一格：兩者高度永遠一樣，進出不推動下面的清單。
        var slot = new Grid();
        slot.Children.Add(covered);
        slot.Children.Add(_bar);
        Slot = slot;

        _done = new DispatcherTimer(DispatcherPriority.Background, slot.Dispatcher) { Interval = SqlAssistChrome.SelectionDoneHold };
        _done.Tick += (_, _) => RestoreDoneIcon();

        _selection.Changed += (_, _) => Update();
        _selection.ActionCompleted += OnActionCompleted;
        Update();
    }

    /// <summary>放在搜尋列那一格：被蓋住的搜尋列與選取工具列疊在一起。</summary>
    public FrameworkElement Slot { get; }

    /// <summary>離開多選時，焦點若在工具列上就交給它（通常是清單的焦點列）。</summary>
    public Action? ReturnFocus { get; set; }

    internal bool IsShown => _shown;

    internal string CountText => _count.Text;

    /// <summary>筆數後面那一段淡色說明；沒有時是空字串。</summary>
    internal string NoteText => _note.Visibility == Visibility.Visible ? _note.Text : "";

    internal bool IsSelectAllEnabled => _selectAll.IsEnabled && _selectAll.Visibility == Visibility.Visible;

    /// <summary>
    /// 背景工作的進度：筆數那一格換成進度，全選與動作讓給「取消」。
    /// </summary>
    /// <remarks>進度只留在工具窗裡：結果已在眼前的動作不走通知。</remarks>
    public void ShowProgress(string text, Action cancel)
    {
        _progress = (text ?? throw new ArgumentNullException(nameof(text)), cancel ?? throw new ArgumentNullException(nameof(cancel)));
        Update();
    }

    public void ClearProgress()
    {
        if (_progress is null) return;
        _progress = null;
        Update();
    }

    private void Update()
    {
        var active = _selection.IsActive;
        SetShown(active);
        var progress = active ? _progress : null;
        var text = progress?.Text ?? CountLabel();
        if (active && text != _countText && _countText.Length != 0 && _shown && progress is null)
            SqlAssistChrome.PlayStatusPop(_count, _motion);
        _countText = active ? text : "";
        _count.Text = text;
        AutomationProperties.SetName(_count, text);

        // 還有沒載入的列時才補一句已載入幾筆：全部都在畫面上時，「已選全部 N 筆」已經說完了。
        var note = progress is null && _selection.IsAllMatching && _selection.HasMore
            ? "已載入 " + Format(_selection.LoadedCount) + " 筆，其餘在執行動作時讀取"
            : "";
        _note.Text = note;
        _note.ToolTip = note.Length == 0 ? null : note;
        _note.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var busy = progress is not null;
        if (!busy && _cancel.IsKeyboardFocusWithin) ReturnFocus?.Invoke();
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _selectAll.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _actions.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _selectAll.IsEnabled = active && !_selection.IsAllMatching;
        foreach (var (action, button, _) in _buttons)
            button.IsEnabled = active && !_selection.IsBusy && action.CanExecute();
    }

    /// <summary>全部符合而且還有沒載入的列時說不出確切筆數，只說「全部」。</summary>
    private string CountLabel() => !_selection.IsAllMatching ? "已選 " + Format(_selection.Count) + " 筆"
        : _selection.HasMore ? "已選全部符合的項目"
        : "已選全部 " + Format(_selection.Count) + " 筆";

    private static string Format(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        if (shown)
        {
            _bar.Visibility = Visibility.Visible;
            _covered.IsEnabled = false;
        }
        else
        {
            _covered.IsEnabled = true;
            RestoreDoneIcon();
            // 工具列收起前焦點若在它上面（按了 ✕），交回清單；否則焦點會掉到沒有人接的地方。
            if (_bar.IsKeyboardFocusWithin) ReturnFocus?.Invoke();
        }

        SqlAssistChrome.PlaySelectionBar(_bar, _covered, shown, _motion, () => _shown == shown,
            () => _bar.Visibility = Visibility.Collapsed);
    }

    private void OnBarKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = e.KeyboardDevice.Modifiers;
        if (e.Key == Key.Escape && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _selection.Clear();
        }
        else if (_selection.TryInvokeShortcut(e.Key, modifiers)) e.Handled = true;
    }

    /// <summary>成功的動作把自己的圖示換成打勾停一下；失敗不換，原因已經寫在狀態列。</summary>
    private void OnActionCompleted(SqlSelectionAction action, bool succeeded)
    {
        if (!succeeded || !_shown) return;
        foreach (var (candidate, _, glyph) in _buttons)
        {
            if (!ReferenceEquals(candidate, action)) continue;
            RestoreDoneIcon();
            _doneGlyph = glyph;
            _doneIcon = glyph.Icon;
            glyph.Icon = SqlIcon.Done;
            SqlAssistChrome.PlayStatusPop(glyph, _motion);
            _done.Start();
            return;
        }
    }

    private void RestoreDoneIcon()
    {
        _done.Stop();
        if (_doneGlyph is null) return;
        _doneGlyph.Icon = _doneIcon;
        _doneGlyph = null;
    }
}
