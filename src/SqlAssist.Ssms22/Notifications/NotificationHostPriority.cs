using System;
using System.Collections.Generic;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 卡片該掛在哪一個宿主上。
/// </summary>
/// <remarks>
/// 順序：有焦點的 SQL 編輯區 → 有焦點的 SqlAssist 視窗 → 最後用過的 SQL 編輯區。
/// 第三級保留原本的行為：焦點移到物件總管或別的應用程式，提示不該因此消失。
/// 但它排在視窗之後，否則 SQL Memory 工具窗與對話框永遠搶不到卡片——最後用過的編輯區
/// 幾乎總是看得見。都沒有就不顯示，不退回主視窗。
///
/// 與 <see cref="NotificationSurfaceController"/> 分開是為了測得到：這裡只有旗標。
/// </remarks>
internal static class NotificationHostPriority
{
    /// <summary>數字小的優先；null 表示不接卡片。</summary>
    internal static int? Rank(NotificationSurfaceHostKind kind, NotificationSurfaceHostActivity activity, bool visible)
    {
        if (!visible) return null;
        return activity switch
        {
            NotificationSurfaceHostActivity.Focused => kind == NotificationSurfaceHostKind.Editor ? 0 : 1,
            NotificationSurfaceHostActivity.Recent => 2,
            _ => null,
        };
    }

    /// <summary>挑出優先的宿主；同級時留在目前的擁有者上，不在兩個宿主之間來回搬。</summary>
    internal static INotificationSurfaceHost? Select(IReadOnlyList<INotificationSurfaceHost> hosts, INotificationSurfaceHost? owner)
    {
        if (hosts is null) throw new ArgumentNullException(nameof(hosts));
        INotificationSurfaceHost? best = null;
        var bestRank = int.MaxValue;
        for (var index = 0; index < hosts.Count; index++)
        {
            var host = hosts[index];
            // 先問焦點再問可見度：視窗宿主的可見度要往上找圖層，非作用中的不必付那一次。
            var activity = host.Activity;
            if (activity == NotificationSurfaceHostActivity.Inactive ||
                Rank(host.Kind, activity, host.IsVisible) is not { } rank) continue;
            if (rank < bestRank || (rank == bestRank && ReferenceEquals(host, owner)))
            { best = host; bestRank = rank; }
        }

        return best;
    }
}
