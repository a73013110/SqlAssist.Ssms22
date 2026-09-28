using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Notifications;

/// <summary>
/// 平台邊界攔下的未預期例外，除了紀錄檔之外也讓使用者看得到一則失敗。
/// </summary>
/// <remarks>
/// 只寫紀錄檔的症狀是「功能安靜地不作用」：預覽建不起來、按向右鍵沒有反應，使用者要自己想到
/// 去翻紀錄檔才知道那是錯誤而不是設計。通知只說「出錯了、這一次略過、堆疊在紀錄檔」，
/// 不帶例外內容（通知不保存例外），操作名稱也不上畫面：那是寫給開發者看的紀錄用語，不在地化。
///
/// 兩道閘各擋一種洪水：
/// <list type="bullet">
/// <item>同一個操作 <see cref="Interval"/> 內只送一次。按鍵處理常式壞掉時每按一次鍵失敗一次，
/// 通知島自己的重畫壞掉時則是「送出 → 重畫 → 失敗 → 再送出」的迴圈。</item>
/// <item>送出的當下在同一條執行緒上又失敗（通知的訂閱者同步丟例外）不再送，否則遞迴到堆疊溢位。</item>
/// </list>
/// </remarks>
public sealed class UnexpectedFailureNotice
{
    /// <summary>同一個操作兩則通知之間至少隔多久。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public static UnexpectedFailureNotice Default { get; } = new(NotificationCenter.Default);

    [ThreadStatic] private static bool t_posting;

    private readonly NotificationCenter _center;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, DateTimeOffset> _lastPosted = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public UnexpectedFailureNotice(NotificationCenter center, Func<DateTimeOffset>? clock = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>送出一則失敗；被節流或正在送出時回傳 false。</summary>
    /// <param name="operation">紀錄檔用的操作名稱，只當節流的鍵。</param>
    public bool Report(string operation)
    {
        if (t_posting) return false;

        lock (_gate)
        {
            var now = _clock();
            if (_lastPosted.TryGetValue(operation ?? "", out var last) && now - last < Interval) return false;
            _lastPosted[operation ?? ""] = now;
        }

        t_posting = true;
        try
        {
            // 種類說不出是哪個子系統，歸 Unclassified；失敗有自己的可見度通道，不受種類開關影響。
            _center.Post(NotificationCatalog.RunningInternalStep, NotificationKind.Unclassified, NotificationOrigin.Ambient,
                NotificationLevel.Notice, NotificationStatus.Failed, message: NotificationCatalog.ResultUnexpected);
            return true;
        }
        finally
        {
            t_posting = false;
        }
    }
}
