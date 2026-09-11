using System;
using System.Collections.Generic;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 決定通知卡片該顯示什麼；每個 SSMS 視窗一份。
/// </summary>
/// <remarks>
/// 可見度、合併、措辭與關閉狀態集中在這裡，<see cref="NotificationAdornment"/> 只剩宿主
/// 與定位。分散在每個編輯區的版本要嘛每個編輯區各算一次同一份清單，要嘛把「哪些通知
/// 已經被關掉」寫成靜態欄位——後者正是關掉一個編輯區的提示會關掉全部的原因。
///
/// 狀態只在 UI 執行緒上讀寫（<see cref="Current"/> 由編輯器的刷新路徑呼叫）；
/// 通知來源的 <see cref="NotificationCenter.Changed"/> 可能來自任何執行緒，因此這裡
/// 只把事件轉發出去，不在事件上碰狀態。
/// </remarks>
internal sealed class NotificationHost
{
    /// <summary>失敗與降級至少保留這麼久，即使成功的保留時間更短。</summary>
    private const int MinimumFailureRetention = 6000;

    public static NotificationHost Default { get; } = new();

    /// <summary>通知內容可能變了；作用中的編輯區才需要重算。</summary>
    public event EventHandler? Changed;

    private IReadOnlyList<NotificationItem>? _source;
    private IReadOnlyList<NotificationCardItem> _items = Array.Empty<NotificationCardItem>();
    private SqlAssistSettings? _settings;

    /// <summary>
    /// 已經被關閉的最後一個通知 Id；比它新的工作仍會出現。
    /// </summary>
    /// <remarks>
    /// 關閉刻意是全域的：提示同一時間只有一份，跟著作用中的編輯區走，使用者按下的
    /// 那個叉號指的是「這一批我看完了」，不是「這個分頁不要再顯示」。因此它在這裡是
    /// 一份具名的呈現狀態，不是散在編輯區類別上的靜態可變欄位。
    /// </remarks>
    private long _dismissedThrough;
    private bool _defaultExpanded = true;
    private DateTimeOffset? _oldest;

    /// <summary>明細展開與否；跟著提示走，切換編輯區不會忽然收合。</summary>
    public bool Expanded { get; private set; } = true;

    private readonly NotificationCenter _center;

    private NotificationHost() : this(NotificationCenter.Default) { }

    /// <summary>測試用：換一個通知來源，其餘行為與正式的那一份相同。</summary>
    internal NotificationHost(NotificationCenter center)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _center.Changed += OnChanged;
    }

    // 不包 SqlAssistPlatformGuard：轉發本身碰不到平台，而訂閱端（adornment）就是平台邊界，
    // 在那裡包才記得到是哪個編輯區。這一份因此只依賴 Core 與 UI，測試能直接編譯它。
    private void OnChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    public void Toggle() => Expanded = !Expanded;

    /// <summary>關閉目前這一批；不取消工作，之後的新工作仍會通知。</summary>
    public void Dismiss(SqlAssistSettings settings)
    {
        var items = Snapshot(settings, retain: false);
        for (var index = 0; index < items.Count; index++)
            if (items[index].Id > _dismissedThrough) _dismissedThrough = items[index].Id;
        Invalidate();
    }

    /// <summary>離開提示後把暫停的期限續跑，不留下永遠不到期的結果。</summary>
    public void Release(SqlAssistSettings settings) => Snapshot(settings, retain: false);

    /// <summary>這一批是不是都還在延遲顯示的時間內。</summary>
    public bool WithinDelay(TimeSpan delay, DateTimeOffset now) => _oldest is { } oldest && now - oldest < delay;

    /// <summary>
    /// 目前該畫的那幾列；來源與設定都沒變時回傳上一次那一份。
    /// </summary>
    /// <param name="retain">滑鼠或鍵盤焦點還在提示內，期限暫停。</param>
    public IReadOnlyList<NotificationCardItem> Current(SqlAssistSettings settings, bool retain)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        // 使用者改了「預設展開明細」才跟著改；自己按過的展開狀態不被週期刷新蓋回去。
        if (_defaultExpanded != settings.NotificationExpanded)
        { _defaultExpanded = settings.NotificationExpanded; Expanded = _defaultExpanded; }
        var source = Snapshot(settings, retain);
        // 來源沒變就是同一個陣列；每 100 ms 重跑一次篩選、合併與投影只是把沒發生的事重算。
        if (ReferenceEquals(source, _source) && ReferenceEquals(settings, _settings)) return _items;
        _source = source; _settings = settings;
        var projection = Project(source, settings, _dismissedThrough);
        _items = projection.Items; _oldest = projection.Oldest;
        return _items;
    }

    /// <summary>下一次 <see cref="Current"/> 重新投影，即使來源與設定的參考都沒變。</summary>
    private void Invalidate() => _source = null;

    private IReadOnlyList<NotificationItem> Snapshot(SqlAssistSettings settings, bool retain) =>
        _center.Snapshot(
            TimeSpan.FromMilliseconds(settings.NotificationRetention),
            TimeSpan.FromMilliseconds(Math.Max(MinimumFailureRetention, settings.NotificationRetention)),
            retain);

    /// <summary>篩掉看不見的、合併重複的，再翻成卡片認得的記錄。</summary>
    /// <remarks>
    /// 先篩後併：合併鍵不含可見度，隱藏的工作併進來會讓 ×N 大於畫面上真正發生過的次數。
    /// </remarks>
    internal static NotificationProjection Project(
        IReadOnlyList<NotificationItem> items, SqlAssistSettings settings, long dismissedThrough = 0)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var visible = new List<NotificationItem>(items.Count);
        DateTimeOffset? oldest = null;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.Id <= dismissedThrough || !NotificationVisibility.Includes(item, settings)) continue;
            visible.Add(item);
            if (oldest is null || item.Started < oldest) oldest = item.Started;
        }

        var merged = NotificationMerge.Collapse(visible);
        var cards = new NotificationCardItem[merged.Count];
        for (var index = 0; index < merged.Count; index++) cards[index] = ToCardItem(merged[index]);
        return new NotificationProjection(cards, oldest);
    }

    private static NotificationCardItem ToCardItem(NotificationItem item) => new(
        item.Id,
        NotificationCatalog.Headline(item),
        item.Subject,
        item.Document,
        item.Source,
        NotificationCatalog.ResultMessage(item),
        // Degraded 還沒有專屬視覺，暫時落在取消的叉號上；警告圖示與警告色需要新的主題筆刷。
        item.Status switch
        {
            NotificationStatus.Running => NotificationVisualStatus.Running,
            NotificationStatus.Succeeded => NotificationVisualStatus.Completed,
            NotificationStatus.Failed => NotificationVisualStatus.Failed,
            _ => NotificationVisualStatus.Canceled,
        },
        NotificationCatalog.StatusText(item.Status),
        item.Repeat);
}

/// <summary>一輪投影的結果：畫面上的那幾列，以及其中最早啟動的時間。</summary>
/// <remarks>延遲顯示要看的是看得見的那些工作，隱藏的與已關閉的不算在內。</remarks>
internal readonly struct NotificationProjection
{
    public NotificationProjection(IReadOnlyList<NotificationCardItem> items, DateTimeOffset? oldest)
    { Items = items; Oldest = oldest; }

    public IReadOnlyList<NotificationCardItem> Items { get; }
    public DateTimeOffset? Oldest { get; }
}
