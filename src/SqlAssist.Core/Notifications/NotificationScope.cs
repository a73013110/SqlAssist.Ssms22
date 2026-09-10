using System;
using System.Threading;

namespace SqlAssist.Core.Notifications;

/// <summary>一件事的存續期間；釋放時寫回結果。</summary>
public sealed class NotificationScope : IDisposable
{
    private readonly NotificationCenter _owner;
    private readonly NotificationScope? _parent;
    private readonly bool _ownsItem;
    private readonly bool _ambient;
    private int _status = (int)NotificationStatus.Succeeded;
    private int _disposed;

    internal NotificationScope(NotificationCenter owner, long id, NotificationScope? parent, bool ownsItem,
        bool ambient = false)
    {
        _owner = owner; Id = id; _parent = parent; _ownsItem = ownsItem; _ambient = ambient;
    }

    internal long Id { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>這一份是不是自己的項目；合併進外層工作時為 false。</summary>
    /// <remarks>
    /// 呼叫端用它判斷「我是這一輪最外層的那一個」。中繼資料的跨資料庫與連結伺服器
    /// 提示靠這一條決定要不要開：巢狀查詢已經併進外層，跟著開的話同一次載入會冒出
    /// 好幾列一模一樣的遠端提示。
    /// </remarks>
    public bool OwnsItem => _ownsItem;

    /// <summary>回報簡短進度訊息；呼叫端不得放入 SQL、路徑、認證或例外內容。</summary>
    public void Report(string message)
    {
        // 限制常駐記憶體；已結束或合併的子工作不覆寫主要工作的訊息。
        message ??= "";
        if (!IsDisposed && _ownsItem)
            _owner.Report(Id, message.Length > NotificationCenter.MessageLimit
                ? message.Substring(0, NotificationCenter.MessageLimit)
                : message);
    }

    /// <summary>
    /// 這件事自己失敗了。
    /// </summary>
    /// <remarks>
    /// 合併的子工作失敗只把父工作標成降級：中繼資料查詢對 <c>DbException</c> 一律降級，
    /// 若沿用上傳失敗，一次可正常降級的欄位查詢就會讓整個「按 Tab 展開」顯示為失敗。
    /// 只有擁有項目的工作自己的例外才是失敗。
    /// </remarks>
    public void Fail()
    {
        if (_ownsItem) Raise(NotificationStatus.Failed);
        else _parent?.Degrade();
    }

    /// <summary>主要結果拿到了，但有一部分資料取不到。</summary>
    public void Degrade()
    {
        if (_ownsItem) Raise(NotificationStatus.Degraded);
        else _parent?.Degrade();
    }

    public void Cancel()
    {
        Raise(NotificationStatus.Canceled);
        if (!_ownsItem) _parent?.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_ownsItem) return;
        // 並行工作沒有接手環境父工作，還回去會把真正的父工作從堆疊上抹掉。
        if (_ambient) _owner.Leave(_parent);
        _owner.Finish(Id, (NotificationStatus)Volatile.Read(ref _status));
    }

    /// <summary>只往上升級。並行子查詢中，取消不能把另一項剛回報的失敗覆蓋掉。</summary>
    private void Raise(NotificationStatus status)
    {
        var rank = NotificationCenter.Rank(status);
        while (true)
        {
            var current = Volatile.Read(ref _status);
            if (NotificationCenter.Rank((NotificationStatus)current) >= rank) return;
            if (Interlocked.CompareExchange(ref _status, (int)status, current) == current) return;
        }
    }
}
