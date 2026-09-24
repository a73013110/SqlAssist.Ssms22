using System;
using System.Collections.Generic;

namespace SqlAssist.Ssms22.UI;

/// <summary>提醒的嚴重度；決定左側語意圖示與排序，不決定按鈕顏色。</summary>
internal enum NotificationPromptSeverity { Info, Warning, Error }

/// <summary>提醒上的一顆按鈕。</summary>
/// <param name="Id">穩定識別字；按下後原樣交回呈現端。</param>
/// <param name="Label">按鈕上的字，已經是目錄裡的措辭。</param>
/// <param name="Primary">主要動作：放在最右、給淡底；每則最多一顆。</param>
internal sealed record NotificationPromptAction(string Id, string Label, bool Primary);

/// <summary>通知島要畫的一則提醒。</summary>
/// <remarks>
/// 與 <see cref="NotificationActivityItem"/> 同一個原則：島嶼只認得這個記錄，不認得
/// <c>Core/Notifications</c>，措辭、可見度與排序都在呈現端決定完才交過來。
/// </remarks>
/// <param name="Id">這則提醒的身分；處理時連同按鈕識別字交回。</param>
/// <param name="Position">排序後的第幾則（從 1 起算）；疊起來時右上角顯示「Position/Count」。</param>
/// <param name="Count">目前看得見的提醒總數。</param>
internal sealed record NotificationPromptItem(
    long Id,
    string Title,
    string Message,
    NotificationPromptSeverity Severity,
    IReadOnlyList<NotificationPromptAction> Actions,
    int Position,
    int Count);

/// <summary>通知島一輪要畫的全部內容。</summary>
/// <param name="Activities">活動列，已經篩選、合併並翻成 <see cref="NotificationActivityItem"/>；依啟動順序，完成不重排。</param>
/// <param name="Summary">膠囊上的那一行。</param>
/// <param name="Prompts">依嚴重度、再依時間新到舊排好的提醒。</param>
internal sealed record NotificationIslandContent(
    IReadOnlyList<NotificationActivityItem> Activities,
    string Summary,
    IReadOnlyList<NotificationPromptItem> Prompts)
{
    public static NotificationIslandContent Empty { get; } =
        new(Array.Empty<NotificationActivityItem>(), "", Array.Empty<NotificationPromptItem>());

    public int Running => Count(NotificationVisualStatus.Running);
    public int Completed => Count(NotificationVisualStatus.Completed);
    public int Failed => Count(NotificationVisualStatus.Failed);

    /// <summary>
    /// 這一批整體的狀態：還有工作在跑就是執行中，否則有失敗就是失敗，再來才是成功。
    /// </summary>
    /// <remarks>膠囊、清單抬頭與提醒卡的附條都畫這一個，三處對「現在怎樣了」的回答才一致。</remarks>
    public NotificationVisualStatus Status =>
        Running > 0 ? NotificationVisualStatus.Running
        : Failed > 0 ? NotificationVisualStatus.Failed
        : Completed > 0 ? NotificationVisualStatus.Completed
        : Activities.Count > 0 ? NotificationVisualStatus.Canceled
        : NotificationVisualStatus.Pending;

    private int Count(NotificationVisualStatus status)
    {
        var count = 0;
        foreach (var item in Activities)
            if (item.Status == status) count++;
        return count;
    }
}
