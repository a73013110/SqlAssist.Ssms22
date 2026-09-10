using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Notifications;

/// <summary>本次工作階段每一件事的呼叫次數與耗時。</summary>
/// <remarks>
/// 統計不受可見度影響：隱藏的種類、<see cref="NotificationLevel.Trace"/> 與快取命中
/// 一樣計入。只開詳細度而不做統計答不出「哪些動作在重複」——高頻工作在畫面上
/// 只是一片閃爍，而預設隱藏的四個高頻種類根本不會出現在那裡。
///
/// 只留次數與耗時，不保存 SQL、連線字串、認證或例外內容，與診斷紀錄的政策一致。
/// </remarks>
public sealed class NotificationDigest
{
    /// <summary>超出上限後所有新工作合併去的那一列。</summary>
    public const string OtherTitle = "超出上限的其他工作";

    /// <summary>
    /// 分開統計的鍵數上限。
    /// </summary>
    /// <remarks>
    /// <see cref="NotificationItem.Subject"/> 是物件限定名稱，鍵的數量跟著使用者碰過的
    /// 物件成長，沒有上限就是一整天下來只增不減。超過之後併成一列，統計仍然是完整的
    /// 呼叫次數，只是分不出是哪幾件事。
    /// </remarks>
    private const int DefaultCapacity = 200;

    private readonly object _gate = new();
    private readonly Dictionary<(NotificationKind, string, string), Bucket> _buckets = new();
    private readonly int _capacity;
    private Bucket? _other;

    public NotificationDigest(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>記下完成的一次呼叫。</summary>
    /// <remarks>
    /// 一次呼叫一筆，不看 <see cref="NotificationItem.Repeat"/>：合併只影響畫面，
    /// 進到這裡的是 <see cref="NotificationCenter"/> 逐次交出的最終快照。
    /// </remarks>
    public void Record(NotificationItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (item.Status == NotificationStatus.Running) return;
        var elapsed = item.Finished is { } finished && finished > item.Started
            ? finished - item.Started
            : TimeSpan.Zero;
        lock (_gate)
        {
            var key = (item.Kind, item.Title, item.Subject);
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                if (_buckets.Count >= _capacity) bucket = _other ??= new Bucket();
                else _buckets.Add(key, bucket = new Bucket());
            }

            bucket.Add(elapsed, item.Status);
        }
    }

    /// <summary>目前的統計；「其他」固定排在最後。</summary>
    public IReadOnlyList<NotificationDigestEntry> Snapshot(NotificationDigestOrder order = NotificationDigestOrder.Count)
    {
        List<NotificationDigestEntry> entries;
        NotificationDigestEntry? other;
        lock (_gate)
        {
            entries = _buckets
                .Select(pair => pair.Value.ToEntry(pair.Key.Item1, pair.Key.Item2, pair.Key.Item3, isOther: false))
                .ToList();
            other = _other?.ToEntry(NotificationKind.Unclassified, OtherTitle, "", isOther: true);
        }

        // 次要鍵是另一個數字而不是名稱：同樣跑了 30 次的兩件事，先看到慢的那一件才有用。
        entries.Sort((left, right) => order == NotificationDigestOrder.TotalElapsed
            ? Compare(right.Total.CompareTo(left.Total), right.Count.CompareTo(left.Count), left, right)
            : Compare(right.Count.CompareTo(left.Count), right.Total.CompareTo(left.Total), left, right));
        if (other is not null) entries.Add(other);
        return entries;
    }

    private static int Compare(int primary, int secondary, NotificationDigestEntry left, NotificationDigestEntry right)
    {
        if (primary != 0) return primary;
        if (secondary != 0) return secondary;
        var title = string.CompareOrdinal(left.Title, right.Title);
        return title != 0 ? title : string.CompareOrdinal(left.Subject, right.Subject);
    }

    private sealed class Bucket
    {
        private int _count;
        private long _totalTicks;
        private long _maxTicks;
        private int _failed;
        private int _degraded;

        internal void Add(TimeSpan elapsed, NotificationStatus status)
        {
            _count++;
            _totalTicks += elapsed.Ticks;
            if (elapsed.Ticks > _maxTicks) _maxTicks = elapsed.Ticks;
            if (status == NotificationStatus.Failed) _failed++;
            else if (status == NotificationStatus.Degraded) _degraded++;
        }

        internal NotificationDigestEntry ToEntry(NotificationKind kind, string title, string subject, bool isOther) =>
            new(kind, title, subject, _count, TimeSpan.FromTicks(_totalTicks), TimeSpan.FromTicks(_maxTicks),
                _failed, _degraded, isOther);
    }
}
