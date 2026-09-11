using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Notifications;

/// <summary>把重複的完成項目併成一列；只影響畫面。</summary>
/// <remarks>
/// 高頻工作在保留期內每一次都是新的 <see cref="NotificationItem.Id"/>、新的一列，
/// 於是「哪些動作在重複」變成一片閃爍。合併把同一件事收成一列加上重複次數，
/// 而每一次呼叫仍然逐次進統計與詳細診斷，數字不因為畫面收攏而變少。
/// </remarks>
public static class NotificationMerge
{
    /// <summary>
    /// 依 (<see cref="NotificationItem.Kind"/>、<see cref="NotificationItem.Title"/>、
    /// <see cref="NotificationItem.Subject"/>、<see cref="NotificationItem.Document"/>、
    /// <see cref="NotificationItem.Source"/>) 合併。
    /// </summary>
    /// <remarks>
    /// 只合併已完成且結果相同的項目：執行中的各自成列才保得住進度感，
    /// 失敗與降級併進成功則會把唯一看得見的壞消息藏進一個成功的計數裡。
    /// 代表列是群組中最早的那一項，位置與 <see cref="NotificationItem.Id"/> 都沿用它——
    /// 換成最新的那一項，每重複一次就是換一個身分，畫面又回到每次都重畫一列。
    /// 因此耗時顯示的是第一次的；逐次的總計、平均與最大值在工作階段統計。
    /// </remarks>
    public static IReadOnlyList<NotificationItem> Collapse(IReadOnlyList<NotificationItem> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        var merged = new List<NotificationItem>(items.Count);
        var positions = new Dictionary<(NotificationKind, string, string, string, string, NotificationStatus), int>();
        foreach (var item in items)
        {
            if (item.Status == NotificationStatus.Running) { merged.Add(item); continue; }
            var key = (item.Kind, item.Title, item.Subject, item.Document, item.Source, item.Status);
            if (positions.TryGetValue(key, out var position))
                merged[position] = merged[position].WithRepeat(merged[position].Repeat + item.Repeat);
            else
            {
                positions.Add(key, merged.Count);
                merged.Add(item);
            }
        }

        return merged;
    }
}
