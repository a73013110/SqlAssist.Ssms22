using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlAssist.Core.Matching;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 「上一個命中／第幾個／下一個命中」那一組控制項。
/// </summary>
/// <remarks>
/// 與功能無關：拿到的是一份 <see cref="MatchCursor"/>，交出去的是「位置變了」。
/// 誰算出那些命中、要把畫面捲到哪裡，這裡一個字都不知道——SQL Search 的定義預覽是第一個
/// 用它的地方，版本差異與「檢視這一格完整內容」是下一個，而那兩處的命中不是 SQL。
///
/// 自動捲動<b>不</b>由它負責，它只說位置變了。兩件事併在一起的症狀是重設內容那一次也被
/// 當成一次導覽，而使用者還沒按任何按鈕，畫面已經自己跳了一下。
///
/// 沒有鍵盤捷徑：F3 在 SSMS 上是查詢視窗的「找下一個」，而宿主在 WPF 看到按鍵<b>之前</b>
/// 就把它吃掉了（命令路由的 pretranslate），工具窗上的 <c>PreviewKeyDown</c> 一次都不會跑到。
/// 接得住的那一版要去註冊宿主的命令與鍵繫結，而那是另一件事；留著一組按不動的提示，
/// 症狀是使用者照 Tooltip 按了沒反應，以為整個導覽壞了。
///
/// 位置的字是「3 / 7」而不是「第 3 個，共 7 個」：它每按一次就會變，貼在兩顆按鈕中間，
/// 而那個位置容不下一句話。完整的句子留在自動化名稱與 Tooltip 上，螢幕閱讀器唸得到。
/// </remarks>
internal sealed class SqlMatchNavigator : StackPanel
{
    private readonly Button _previous = SqlAssistChrome.CreateIconButton(SqlIcon.PreviousMatch, PreviousLabel);
    private readonly Button _next = SqlAssistChrome.CreateIconButton(SqlIcon.NextMatch, NextLabel);
    private readonly TextBlock _position = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
    private MatchCursor _cursor = MatchCursor.Empty;

    internal const string PreviousLabel = "上一個命中";
    internal const string NextLabel = "下一個命中";

    public SqlMatchNavigator()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        _previous.Click += (_, _) => SqlAssistPlatformGuard.Run("上一個命中", () => Move(forward: false));
        _next.Click += (_, _) => SqlAssistPlatformGuard.Run("下一個命中", () => Move(forward: true));

        // 位置的字本身不可按：它只是一個讀數，做成按鈕會在 Tab 順序裡多一站，而那一站沒有動作。
        _position.TextTrimming = TextTrimming.None;
        _position.Margin = new Thickness(2, 0, 2, 0);
        // 由「1 / 7」換到「10 / 7」時兩顆按鈕不該跟著位移；留住最常見那幾種寬度。
        _position.MinWidth = 34;
        _position.TextAlignment = TextAlignment.Center;

        Children.Add(_previous);
        Children.Add(_position);
        Children.Add(_next);

        Update();
    }

    /// <summary>目前停在第幾個變了；重設命中本身<b>不</b>觸發它。</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>目前那一份游標；沒有命中時是 <see cref="MatchCursor.Empty"/>。</summary>
    /// <remarks>
    /// 名字不叫 <c>Cursor</c>：<see cref="FrameworkElement.Cursor"/> 已經佔了那個名字，
    /// 而它講的是滑鼠指標。隱藏基底成員的那一版編譯得過，讀起來卻像是在設游標的形狀。
    /// </remarks>
    public MatchCursor Matches => _cursor;

    /// <summary>換一份命中；整組收起或展開由命中數決定，不另外問。</summary>
    /// <remarks>
    /// 沒有命中時整組<b>收起</b>而不是停用：兩顆停用的箭頭與一個「0 / 0」浮在工具列上，
    /// 說的是「這裡本來有東西可以按」，而這一筆結果根本沒有可以導覽的位置。
    /// </remarks>
    public void SetCursor(MatchCursor cursor)
    {
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        Update();
    }

    /// <summary>清掉命中；換一列或載入失敗時用。</summary>
    public void Clear() => SetCursor(MatchCursor.Empty);

    /// <summary>往前或往後一個；位置真的變了才回 true，呼叫端據此決定要不要捲。</summary>
    public bool Move(bool forward)
    {
        if (!(forward ? _cursor.MoveNext() : _cursor.MovePrevious())) return false;

        Update();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Update()
    {
        var has = !_cursor.IsEmpty;
        Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;

        _position.Text = _cursor.Position + " / " + _cursor.Count;
        var spoken = "第 " + _cursor.Position + " 個命中，共 " + _cursor.Count + " 個";
        _position.ToolTip = spoken;
        AutomationProperties.SetName(_position, spoken);

        // 只有一處時兩顆按鈕仍然看得見但按不動：收起它們會讓工具列在一處與兩處之間換版面，
        // 而「只有這一處」本身就是使用者要讀到的訊息。
        _previous.IsEnabled = _next.IsEnabled = _cursor.Count > 1;
    }
}
