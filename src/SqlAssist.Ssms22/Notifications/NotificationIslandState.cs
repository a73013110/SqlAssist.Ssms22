using System;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>通知島此刻的形態。</summary>
internal enum NotificationIslandShape
{
    /// <summary>沒有活動也沒有提醒。</summary>
    Hidden,

    /// <summary>有工作在跑：膠囊，帶唯一那一個進度圈。</summary>
    Compact,

    /// <summary>工作都結束了、還在保留期限內：膠囊換成結果圖示。</summary>
    Done,

    /// <summary>活動的明細清單；停駐、鍵盤焦點或按了提醒卡的附條才會到這裡。</summary>
    Expanded,

    /// <summary>一則提醒，沒有活動。</summary>
    Prompt,

    /// <summary>一則提醒，活動縮成卡片底部的附條。</summary>
    PromptWithActivity,

    /// <summary>兩則以上的提醒疊在一起；有活動時最上面那一張帶附條（<see cref="NotificationIslandState.ActivityStrip"/>）。</summary>
    PromptStack,
}

/// <summary>決定形態需要的那幾個數字；由呈現端的島嶼投影算好交過來。</summary>
internal readonly struct NotificationIslandInput
{
    public NotificationIslandInput(int activities, int running, int failed, int prompts)
    {
        Activities = activities; Running = running; Failed = failed; Prompts = prompts;
    }

    public int Activities { get; }
    public int Running { get; }
    public int Failed { get; }
    public int Prompts { get; }
}

/// <summary>
/// 通知島的形態狀態機：只有時間與輸入，沒有 WPF，測試直接餵時間。
/// </summary>
/// <remarks>
/// 停駐 <see cref="ExpandDelay"/> 才展開，移開 <see cref="CollapseDelay"/> 才收回：滑鼠路過
/// 不該讓島嶼彈開，收回也要留時間讓使用者移回來。鍵盤焦點進來就立刻展開，焦點還在就不收。
///
/// 失敗不自動展開：展開會蓋住使用者正在看的東西，而失敗的細節不是非看不可。改成膠囊上的
/// 圖示換成警告，並短震一次（<see cref="ShakeCount"/> 每多一個失敗加一，檢視端看到它變了才播）。
///
/// 有提醒時提醒優先，活動縮成提醒卡底部的附條；按附條暫時切過去看活動（<see cref="Peeking"/>），
/// 清單底部的附條切回提醒，移開後也照一般的收回延遲切回。
/// </remarks>
internal sealed class NotificationIslandState
{
    public static readonly TimeSpan ExpandDelay = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(600);

    private NotificationIslandInput _input;
    private DateTimeOffset? _hoverSince;
    private DateTimeOffset? _leftAt;
    private bool _hovered;
    private bool _focused;
    private bool _expanded;
    private int _failed;

    public NotificationIslandShape Shape { get; private set; } = NotificationIslandShape.Hidden;

    /// <summary>提醒佔著島嶼時，活動縮成提醒卡底部的附條。</summary>
    public bool ActivityStrip { get; private set; }

    /// <summary>按了附條、正在暫時看活動。</summary>
    public bool Peeking { get; private set; }

    /// <summary>活動裡有失敗：膠囊與附條的圖示換成警告。</summary>
    public bool Warning => _input.Failed > 0;

    /// <summary>每多一個失敗加一；檢視端記住上一次的值，變了才短震，不因重畫而重播。</summary>
    public int ShakeCount { get; private set; }

    /// <summary>下一次 <see cref="Tick"/> 可能改變形態的時刻；沒有等待中的轉換時是 null。</summary>
    public DateTimeOffset? Deadline
    {
        get
        {
            if (!_expanded && !_focused && _hoverSince is { } since && CanExpand) return since + ExpandDelay;
            if ((_expanded || Peeking) && _leftAt is { } left) return left + CollapseDelay;
            return null;
        }
    }

    /// <summary>沒有提醒時才以停駐展開活動；有提醒時展開活動要靠附條。</summary>
    private bool CanExpand => _input.Activities > 0 && _input.Prompts == 0;

    /// <summary>換上這一輪的內容；回傳形態或旗標是否改變。</summary>
    public bool Update(NotificationIslandInput input, DateTimeOffset now)
    {
        var before = Capture();
        if (input.Failed > _failed) ShakeCount++;
        _failed = input.Failed;
        _input = input;
        if (input.Activities == 0) { Peeking = false; _expanded = false; }
        if (input.Activities == 0 && input.Prompts == 0) { _hoverSince = null; _leftAt = null; }
        Advance(now);
        return Commit(before);
    }

    public bool PointerEntered(DateTimeOffset now)
    {
        var before = Capture();
        _hovered = true;
        _hoverSince ??= now;
        _leftAt = null;
        Advance(now);
        return Commit(before);
    }

    public bool PointerExited(DateTimeOffset now)
    {
        var before = Capture();
        _hovered = false;
        _hoverSince = null;
        if (!_focused && (_expanded || Peeking)) _leftAt = now;
        Advance(now);
        return Commit(before);
    }

    /// <summary>鍵盤焦點進出島嶼；進來立刻展開，不等停駐延遲。</summary>
    public bool FocusChanged(bool within, DateTimeOffset now)
    {
        var before = Capture();
        _focused = within;
        if (within)
        {
            _leftAt = null;
            if (CanExpand) _expanded = true;
        }
        else if (!_hovered && (_expanded || Peeking)) _leftAt = now;
        Advance(now);
        return Commit(before);
    }

    /// <summary>按了附條：暫時切去看活動；再按一次（清單底部那一條）回到提醒。</summary>
    public bool TogglePeek(DateTimeOffset now)
    {
        var before = Capture();
        if (Peeking) Peeking = false;
        else if (_input.Prompts > 0 && _input.Activities > 0) { Peeking = true; _leftAt = _hovered || _focused ? null : now; }
        Advance(now);
        return Commit(before);
    }

    /// <summary>時間到了才發生的轉換（停駐展開、移開收回）。</summary>
    public bool Tick(DateTimeOffset now)
    {
        var before = Capture();
        Advance(now);
        return Commit(before);
    }

    private void Advance(DateTimeOffset now)
    {
        if (!_expanded && CanExpand && (_focused || (_hoverSince is { } since && now - since >= ExpandDelay)))
            _expanded = true;
        if (_leftAt is { } left && now - left >= CollapseDelay)
        {
            _expanded = false;
            Peeking = false;
            _leftAt = null;
        }

        if (!CanExpand && _input.Prompts == 0) _expanded = false;
        ActivityStrip = _input.Prompts > 0 && _input.Activities > 0 && !Peeking;
        Shape = Resolve();
    }

    private NotificationIslandShape Resolve()
    {
        if (_input.Activities == 0 && _input.Prompts == 0) return NotificationIslandShape.Hidden;
        if (_input.Prompts > 0)
        {
            if (Peeking) return NotificationIslandShape.Expanded;
            if (_input.Prompts > 1) return NotificationIslandShape.PromptStack;
            return _input.Activities > 0 ? NotificationIslandShape.PromptWithActivity : NotificationIslandShape.Prompt;
        }

        if (_expanded) return NotificationIslandShape.Expanded;
        return _input.Running > 0 ? NotificationIslandShape.Compact : NotificationIslandShape.Done;
    }

    private (NotificationIslandShape, bool, bool, int, bool) Capture() => (Shape, ActivityStrip, Peeking, ShakeCount, Warning);

    private bool Commit((NotificationIslandShape, bool, bool, int, bool) before) => before != Capture();
}
