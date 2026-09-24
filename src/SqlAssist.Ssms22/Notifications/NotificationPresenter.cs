using System;
using System.Collections.Generic;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 決定通知島該顯示什麼；整個處理程序一份。
/// </summary>
/// <remarks>
/// 可見度、合併、措辭與關閉狀態集中在這裡，<see cref="NotificationIslandController"/> 只管
/// 何時顯示與浮層錨在哪個視窗上。「哪些活動已經被關掉」是這裡的一份具名狀態，不是散在
/// 視窗類別上的靜態欄位。
///
/// 狀態只在 UI 執行緒上讀寫（<see cref="Island"/> 由控制器的刷新路徑呼叫）；
/// 通知來源的 <see cref="NotificationCenter.Changed"/> 可能來自任何執行緒，因此這裡
/// 只把事件轉發出去，不在事件上碰狀態。
/// </remarks>
internal sealed class NotificationPresenter
{
    /// <summary>失敗與降級至少保留這麼久，即使成功的保留時間更短。</summary>
    private const int MinimumFailureRetention = 6000;

    public static NotificationPresenter Default { get; } = new();

    /// <summary>通知內容可能變了。</summary>
    public event EventHandler? Changed;

    private IReadOnlyList<NotificationItem>? _islandSource;
    private SqlAssistSettings? _islandSettings;
    private NotificationIslandContent _island = NotificationIslandContent.Empty;

    /// <summary>
    /// 已經被關閉的最後一個通知 Id；比它新的工作仍會出現。
    /// </summary>
    /// <remarks>
    /// 叉號指的是「這一批我看完了」，不是取消工作，也不影響提醒。
    /// </remarks>
    private long _dismissedThrough;
    private DateTimeOffset? _oldest;

    private readonly NotificationCenter _center;

    private NotificationPresenter() : this(NotificationCenter.Default) { }

    /// <summary>測試用：換一個通知來源，其餘行為與正式的那一份相同。</summary>
    internal NotificationPresenter(NotificationCenter center)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _center.Changed += OnChanged;
    }

    // 不包 SqlAssistPlatformGuard：轉發本身碰不到平台，訂閱端（控制器）才是平台邊界。
    // 這一份因此只依賴 Core 與 UI，測試能直接編譯它。
    private void OnChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>關閉目前這一批活動；不取消工作，之後的新工作仍會通知。提醒不受影響。</summary>
    public void Dismiss(SqlAssistSettings settings)
    {
        var items = Snapshot(settings, retain: false);
        for (var index = 0; index < items.Count; index++)
            if (!items[index].IsPrompt && items[index].Id > _dismissedThrough) _dismissedThrough = items[index].Id;
        Invalidate();
    }

    /// <summary>使用者按了提醒上的按鈕或叉號（<paramref name="actionId"/> 為 null）。</summary>
    /// <param name="action">按下的那顆按鈕，帶著參數交給派送；叉號時是 null。</param>
    public bool TryResolve(long promptId, string? actionId, out NotificationAction? action) =>
        _center.TryResolve(promptId, actionId, out action);

    /// <summary>離開提示後把暫停的期限續跑，不留下永遠不到期的結果。</summary>
    public void Release(SqlAssistSettings settings) => Snapshot(settings, retain: false);

    /// <summary>這一批是不是都還在延遲顯示的時間內。</summary>
    public bool WithinDelay(TimeSpan delay, DateTimeOffset now) => _oldest is { } oldest && now - oldest < delay;

    /// <summary>
    /// 通知島這一輪的內容：活動列、膠囊摘要與排好的提醒；來源與設定都沒變時回傳上一次那一份。
    /// </summary>
    /// <param name="retain">滑鼠或鍵盤焦點還在島嶼上，活動的期限暫停。</param>
    public NotificationIslandContent Island(SqlAssistSettings settings, bool retain)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var source = Snapshot(settings, retain);
        if (ReferenceEquals(source, _islandSource) && ReferenceEquals(settings, _islandSettings)) return _island;
        _islandSource = source; _islandSettings = settings;
        _island = ProjectIsland(source, settings, _dismissedThrough, out var oldest);
        _oldest = oldest;
        return _island;
    }

    /// <summary>下一次 <see cref="Island"/> 重新投影，即使來源與設定的參考都沒變。</summary>
    private void Invalidate() => _islandSource = null;

    private IReadOnlyList<NotificationItem> Snapshot(SqlAssistSettings settings, bool retain) =>
        _center.Snapshot(
            TimeSpan.FromMilliseconds(settings.NotificationRetention),
            TimeSpan.FromMilliseconds(Math.Max(MinimumFailureRetention, settings.NotificationRetention)),
            retain);

    /// <summary>通知島的投影：活動先篩後併，提醒另外排序。</summary>
    /// <remarks>
    /// 先篩後併：合併鍵不含可見度，隱藏的工作併進來會讓 ×N 大於畫面上真正發生過的次數。
    /// 活動的關閉（<paramref name="dismissedThrough"/>）不影響提醒：叉號在活動上是「這一批看完了」，
    /// 提醒要各自處理。提醒依嚴重度、再依時間新到舊排序，最該先處理的那一則在最上面。
    /// </remarks>
    internal static NotificationIslandContent ProjectIsland(
        IReadOnlyList<NotificationItem> items, SqlAssistSettings settings, long dismissedThrough = 0) =>
        ProjectIsland(items, settings, dismissedThrough, out _);

    /// <param name="oldest">看得見的活動裡最早啟動的時間；延遲顯示只看活動，提醒不等延遲。</param>
    private static NotificationIslandContent ProjectIsland(IReadOnlyList<NotificationItem> items,
        SqlAssistSettings settings, long dismissedThrough, out DateTimeOffset? oldest)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var activities = new List<NotificationItem>(items.Count);
        var prompts = new List<NotificationItem>();
        oldest = null;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!NotificationVisibility.Includes(item, settings)) continue;
            if (item.IsPrompt) prompts.Add(item);
            else if (item.Id > dismissedThrough)
            {
                activities.Add(item);
                if (oldest is null || item.Started < oldest) oldest = item.Started;
            }
        }

        var merged = NotificationMerge.Collapse(activities);
        var activityItems = new NotificationActivityItem[merged.Count];
        for (var index = 0; index < merged.Count; index++) activityItems[index] = ToActivityItem(merged[index]);
        prompts.Sort((left, right) =>
        {
            var severity = right.Severity.CompareTo(left.Severity);
            if (severity != 0) return severity;
            var time = right.Started.CompareTo(left.Started);
            return time != 0 ? time : right.Id.CompareTo(left.Id);
        });
        var promptItems = new NotificationPromptItem[prompts.Count];
        for (var index = 0; index < prompts.Count; index++)
            promptItems[index] = ToPromptItem(prompts[index], index + 1, prompts.Count);
        return new NotificationIslandContent(activityItems, NotificationCatalog.CapsuleSummary(merged), promptItems);
    }

    private static NotificationPromptItem ToPromptItem(NotificationItem item, int position, int count)
    {
        var actions = new NotificationPromptAction[item.Actions.Count];
        for (var index = 0; index < actions.Length; index++)
        {
            var action = item.Actions[index];
            actions[index] = new NotificationPromptAction(action.Id, action.Label, action.Role == NotificationActionRole.Primary);
        }

        return new NotificationPromptItem(item.Id, item.Title, item.Message,
            item.Severity switch
            {
                NotificationSeverity.Error => NotificationPromptSeverity.Error,
                NotificationSeverity.Warning => NotificationPromptSeverity.Warning,
                _ => NotificationPromptSeverity.Info,
            },
            actions, position, count);
    }

    private static NotificationActivityItem ToActivityItem(NotificationItem item) => new(
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
