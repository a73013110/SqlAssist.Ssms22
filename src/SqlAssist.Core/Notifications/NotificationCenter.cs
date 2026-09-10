using System;
using System.Collections.Generic;
using System.Threading;

namespace SqlAssist.Core.Notifications;

/// <summary>全套件共用、可由多執行緒寫入的有限通知紀錄。</summary>
public sealed class NotificationCenter
{
    internal const int MessageLimit = 512;
    private const int HistoryLimit = 100;
    private const int FailureLimit = 30;

    public static NotificationCenter Default { get; } = new();

    /// <summary>本次工作階段的呼叫次數與耗時；可見度不影響它，隱藏的工作一樣計入。</summary>
    public NotificationDigest Digest { get; } = new();

    public event EventHandler? Changed;
    public event Action<NotificationItem>? Completed;
    private readonly object _gate = new();
    private readonly List<NotificationItem> _items = new();
    private readonly Queue<NotificationItem> _failures = new();
    private readonly AsyncLocal<NotificationScope?> _current = new();
    private readonly Func<DateTimeOffset> _clock;
    private long _nextId;
    private DateTimeOffset? _retainedAt;
    private readonly Dictionary<long, TimeSpan> _paused = new();

    /// <summary>內容版本；每一次新增、完成或回報訊息都加一，供 <see cref="Snapshot"/> 判斷有沒有變。</summary>
    private long _version;

    /// <summary>已完成的項數；<see cref="Trim"/> 每次重數就是一趟 O(n)，而它在每次開始與完成時都跑。</summary>
    private int _finished;
    private NotificationItem[]? _cache;
    private long _cacheVersion = -1;
    private TimeSpan _cacheSuccess;
    private TimeSpan _cacheFailure;

    /// <summary>快取到什麼時候為止都還沒有項目到期；在那之前不必重跑清理。</summary>
    private DateTimeOffset _cacheDeadline;

    public NotificationCenter(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>三軸都要明寫，不吃預設值。</summary>
    /// <remarks>
    /// <paramref name="level"/> 沒有預設值是刻意的：帶預設值的版本讓「忘了想」與
    /// 「想過之後選 Info」在程式碼上長得一模一樣，而這兩者在畫面上的差別是
    /// 打字時每一次按鍵都閃一列，還是安靜到只有打開詳細度才看得到。
    /// <paramref name="title"/> 必須是 <see cref="NotificationCatalog"/> 的常數。
    /// </remarks>
    public NotificationScope Begin(string title, NotificationKind kind, NotificationOrigin origin,
        NotificationLevel level, string subject = "", string context = "",
        bool joinParent = false)
    {
        var parent = _current.Value;
        // 同一輪同步巢狀目錄查詢只算一次；獨立背景工作仍有自己的生命週期。
        if (joinParent && parent is not null && !parent.IsDisposed)
            return new NotificationScope(this, parent.Id, parent, ownsItem: false);
        return Create(title, kind, origin, level, subject, context, parent, ambient: true);
    }

    /// <summary>
    /// 與目前工作並行、但不接手巢狀合併的一件事。
    /// </summary>
    /// <remarks>
    /// 給「這一次跳到了另一個資料庫」「這一次走了連結伺服器」這種與查詢同時進行的說明用。
    /// 走一般的 <see cref="Begin"/> 會讓它變成環境父工作，於是查詢底下的巢狀查詢
    /// 全部併進這一列，畫面上就只剩遠端提示、看不到到底在載入什麼。
    /// </remarks>
    public NotificationScope BeginDetached(string title, NotificationKind kind, NotificationOrigin origin,
        NotificationLevel level, string subject = "", string context = "") =>
        Create(title, kind, origin, level, subject, context, _current.Value, ambient: false);

    private NotificationScope Create(string title, NotificationKind kind, NotificationOrigin origin,
        NotificationLevel level, string subject, string context, NotificationScope? parent, bool ambient)
    {
        NotificationScope scope;
        lock (_gate)
        {
            Trim();
            scope = new NotificationScope(this, ++_nextId, parent, ownsItem: true, ambient);
            _items.Add(new NotificationItem(scope.Id, title, subject, context, _clock(),
                NotificationStatus.Running, null, "", kind, origin, level));
            _version++;
            if (ambient) _current.Value = scope;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return scope;
    }

    /// <summary>目前這一批通知；內容沒變又還沒有項目到期時回傳上一次那一份。</summary>
    /// <remarks>
    /// 提示的計時器每 100 ms 問一次，而多數時候什麼都沒發生。每次都重跑到期清理再配置
    /// 一份新陣列，等於把「沒有工作在跑」也做成固定成本。版本號讓沒變的那幾次直接回上一份，
    /// 呼叫端也因此可以用參考相等判斷內容有沒有變，不必自己比對每一項。
    ///
    /// 停留在提示上（<paramref name="retain"/>）時不走快取：那幾次要把暫停的時間累積進去，
    /// 早退會把使用者閱讀的那幾秒算成期限。
    /// </remarks>
    public IReadOnlyList<NotificationItem> Snapshot(TimeSpan successRetention, TimeSpan failureRetention, bool retain = false)
    {
        lock (_gate)
        {
            var now = _clock();
            if (!retain && _retainedAt is null && _cache is { } cached && _cacheVersion == _version &&
                _cacheSuccess == successRetention && _cacheFailure == failureRetention && now < _cacheDeadline)
                return cached;

            // 只暫停完成後的時間；移出時續跑剩餘期限，不立即把閱讀期間的結果清掉。
            if (_retainedAt is { } previous)
                foreach (var item in _items)
                    if (item.Finished is { } finished)
                    {
                        var start = finished > previous ? finished : previous;
                        if (now > start)
                            _paused[item.Id] = (_paused.TryGetValue(item.Id, out var elapsed) ? elapsed : TimeSpan.Zero) + (now - start);
                    }
            _retainedAt = retain ? now : (DateTimeOffset?)null;
            var deadline = DateTimeOffset.MaxValue;
            // 到期清理、暫停紀錄的回收與下一個到期時刻在同一趟走完；分成三趟的版本裡
            // 回收那一趟是 O(n·m)，而它每 100 ms 就跑一次。
            if (!retain && _finished > 0)
                for (var index = _items.Count - 1; index >= 0; index--)
                {
                    var item = _items[index];
                    if (item.Finished is not { } end) continue;
                    var paused = _paused.TryGetValue(item.Id, out var elapsed) ? elapsed : TimeSpan.Zero;
                    // 降級與失敗一樣值得多看幾秒，沿用較長的期限。
                    var retention = Rank(item.Status) >= Rank(NotificationStatus.Degraded) ? failureRetention : successRetention;
                    var age = now - end - paused;
                    if (age < retention)
                    {
                        // 保留期限可以是 TimeSpan.MaxValue，到期時刻要用剩餘時間比較才不會溢位。
                        var remaining = retention - age;
                        if (remaining < deadline - now) deadline = now + remaining;
                        continue;
                    }

                    _items.RemoveAt(index);
                    _paused.Remove(item.Id);
                    _finished--;
                }

            _cache = _items.ToArray();
            _cacheVersion = _version;
            _cacheSuccess = successRetention;
            _cacheFailure = failureRetention;
            _cacheDeadline = deadline;
            return _cache;
        }
    }

    public IReadOnlyList<NotificationItem> RecentFailures
    { get { lock (_gate) return _failures.ToArray(); } }

    /// <summary>結果只往上升級：失敗 &gt; 降級 &gt; 取消 &gt; 成功。</summary>
    internal static int Rank(NotificationStatus status) => status switch
    {
        NotificationStatus.Failed => 4,
        NotificationStatus.Degraded => 3,
        NotificationStatus.Canceled => 2,
        NotificationStatus.Succeeded => 1,
        _ => 0,
    };

    internal void Leave(NotificationScope? parent) => _current.Value = parent;

    private void Trim()
    {
        // 沒有編輯器或關掉提示時也有上限；執行中的工作不能被截掉。
        // 已完成的項數用計數器維護：每次開始與完成都會走到這裡，重數一遍就是固定的 O(n)。
        while (_finished > HistoryLimit)
        {
            var index = _items.FindIndex(x => x.Finished.HasValue);
            if (index < 0) return;
            _paused.Remove(_items[index].Id);
            _items.RemoveAt(index);
            _finished--;
        }
    }

    internal void Finish(long id, NotificationStatus status)
    {
        NotificationItem item;
        lock (_gate)
        {
            var index = _items.FindIndex(x => x.Id == id);
            if (index < 0) return;
            var old = _items[index];
            if (Rank(old.Status) > Rank(status)) status = old.Status;
            item = old.With(status, _clock(), old.Message);
            _items[index] = item;
            if (old.Finished is null) _finished++;
            _version++;
            if (status == NotificationStatus.Failed)
            {
                _failures.Enqueue(item);
                while (_failures.Count > FailureLimit) _failures.Dequeue();
            }
            Trim();
        }
        // 診斷與統計在鎖外接收最終快照，通知篩選不影響紀錄，也不把寫檔成本放進活動鎖。
        Digest.Record(item);
        Completed?.Invoke(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Report(long id, string message)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(x => x.Id == id && !x.Finished.HasValue);
            if (index < 0 || _items[index].Message == message) return;
            _items[index] = _items[index].With(_items[index].Status, null, message);
            _version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
