using System;
using System.Collections.Generic;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Notifications;

/// <summary>
/// 所有通知標題與完成後敘述的唯一出處。
/// </summary>
/// <remarks>
/// 文案契約：<see cref="NotificationItem.Title"/> 是動詞開頭的現在進行式短語、不帶參數的一句，
/// 不含物件名稱、不含狀態、不加標點。物件限定名稱放 <see cref="NotificationItem.Subject"/>，
/// 發起的文件放 <see cref="NotificationItem.Document"/>，資料庫放 <see cref="NotificationItem.Source"/>。
///
/// 標題與過去式寫在 <c>NotificationCatalog.*.resjson</c>（<c>X</c> 與 <c>XDone</c> 一對），
/// 產生的屬性只是從陣列取一句，不配置。不帶參數而不是內插字串，是因為通知在熱路徑上：建議清單每按一次鍵開一次，
/// 中繼資料每一條查詢開一次。組字串的版本即使通知被可見度篩掉也已經配置完畢，
/// 而被篩掉正是預設狀態——高頻的四個種類預設就是關著的。
///
/// 完成後的措辭也集中在這裡：呼叫端只交出三軸與主體，「成功說什麼、降級說什麼、
/// 失敗要不要指去診斷、耗時到幾秒才值得寫出來」全部由這一份決定。分散在呼叫端的版本
/// 會讓同一種結果在畫面上出現好幾種說法，而使用者分不出那是不同的事還是同一件事。
/// </remarks>
public static partial class NotificationCatalog
{
    /// <summary>耗時到這個門檻才寫進敘述；比這短的數字只是雜訊。</summary>
    private static readonly TimeSpan ElapsedThreshold = TimeSpan.FromSeconds(1);

    /// <summary>同一行裡兩段文字之間的分隔。</summary>
    private const string Separator = " · ";

    // 標題（X）與它的過去式（XDone）是 resjson 產生的屬性；成對的才會被 NotificationTitle 認成標題。
    // 提醒的標題不受動詞開頭的契約限制：它不會轉過去式，回答的是「要決定什麼」。
    // 按鈕識別字在 NotificationActionIds；其餘措辭是屬性或方法。

    /// <summary>有新版可以下載；同鍵只留最新那一版。叉號就是「稍後」，不另設按鈕。</summary>
    public static NotificationPrompt UpdateAvailablePrompt(string version, string releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException(null, nameof(version));
        return new NotificationPrompt("update", () => UpdatePromptTitle, () => UpdatePromptMessage(version),
            NotificationSeverity.Info,
            new NotificationAction(NotificationActionIds.UpdateSkip, () => UpdateSkip, NotificationActionRole.Secondary, version),
            new NotificationAction(NotificationActionIds.UpdateDownload, () => UpdateDownload, NotificationActionRole.Primary, releaseUrl ?? ""));
    }

    /// <summary>容量越過警戒；<paramref name="reason"/> 是 Core 給的用量說明。</summary>
    public static NotificationPrompt SqlMemoryCapacityPrompt(string reason) =>
        new("sqlmemory.capacity", () => CapacityPromptTitle,
            string.IsNullOrWhiteSpace(reason) ? () => CapacityPromptMessage : () => reason,
            NotificationSeverity.Warning,
            new NotificationAction(NotificationActionIds.SqlMemoryOpenMaintenance, () => OpenMaintenance, NotificationActionRole.Primary));

    /// <summary>第一次真正開始擷取；不能安靜地開始記錄使用者的 SQL。</summary>
    /// <remarks>
    /// 說明（<see cref="SqlMemoryFirstCaptureNotice"/>）只說「在這台電腦」與去哪裡看、去哪裡關，不寫路徑：
    /// 通知一律不放路徑與檔名，而使用者真正要的是下一步按哪裡。
    /// </remarks>
    public static NotificationPrompt SqlMemoryFirstCapturePrompt() =>
        new("sqlmemory.first-capture", () => FirstCapturePromptTitle, () => SqlMemoryFirstCaptureNotice,
            NotificationSeverity.Info,
            new NotificationAction(NotificationActionIds.SqlMemoryOpen, () => OpenSqlMemory, NotificationActionRole.Primary));

    /// <summary>「關於與診斷」的測試提醒；鍵依嚴重度分開，三種一起送就是疊層。</summary>
    public static NotificationPrompt RehearsalPrompt(NotificationSeverity severity) =>
        new("rehearsal." + severity.ToString().ToLowerInvariant(),
            severity switch
            {
                NotificationSeverity.Error => () => RehearsalErrorTitle,
                NotificationSeverity.Warning => () => RehearsalWarningTitle,
                _ => () => RehearsalInfoTitle,
            },
            () => RehearsalMessage, severity,
            new NotificationAction(NotificationActionIds.RehearsalAcknowledge, () => RehearsalAcknowledge, NotificationActionRole.Primary));

    /// <summary>活動清單抬頭與多項膠囊共用的那一行：「N 項工作 · 成功數/總數」；分子只算成功。</summary>
    /// <remarks>
    /// 兩處說法不同時，膠囊變形成清單的那一刻文字會整句換掉，讀起來像換了一件事。
    ///
    /// 措辭是 <c>ProgressText</c>；包一層是為了保住 (成功數, 總數) 的參數順序，產生的方法依佔位符
    /// 出現的順序排參數，兩個都是數字，順序錯了編譯器也看不出來。
    /// </remarks>
    public static string ProgressSummary(int succeeded, int total) => ProgressText(total, succeeded);

    // 清單抬頭右側的失敗標記（FailureSummary）、暫看活動時底部那一條（PendingPrompts）、
    // 提醒卡附條的「查看」（ViewActivities）與「回到提醒」（BackToPrompts）、活動清單叉號的
    // ToolTip（DismissActivities）都是 resjson 產生的成員。

    /// <summary>多則提醒疊在一起時右上角那一格：「1/3」。</summary>
    public static string PromptPosition(int position, int count) =>
        SqlText.Number(position) + "/" + SqlText.Number(count);

    /// <summary>
    /// 通知島收成膠囊時的那一行。
    /// </summary>
    /// <remarks>
    /// 只有一項時說是哪一件事（「標題 · 主體」）；兩項以上改說規模與進度，因為膠囊寬度
    /// 放不下兩個標題，挑其中一個又會讓使用者以為只有那一件。多項時就是清單抬頭那一句
    /// （<see cref="ProgressSummary"/>）。傳入的是合併且篩選過的列。
    /// </remarks>
    public static string CapsuleSummary(IReadOnlyList<NotificationItem> activities)
    {
        if (activities is null) throw new ArgumentNullException(nameof(activities));
        if (activities.Count == 0) return "";
        if (activities.Count == 1)
        {
            var item = activities[0];
            return item.Subject.Length > 0 ? Headline(item) + Separator + item.Subject : Headline(item);
        }

        var succeeded = 0;
        foreach (var item in activities)
            if (item.Status == NotificationStatus.Succeeded) succeeded++;
        return ProgressSummary(succeeded, activities.Count);
    }

    /// <summary>
    /// 畫面上那一列的主要文字：完成後轉過去式並視情況附上耗時。
    /// </summary>
    /// <remarks>
    /// 只有成功才轉過去式。降級、失敗與取消留在現在進行式，因為那一列右邊接著的是
    /// <see cref="StatusText"/>——「已載入欄位與定義 · 失敗」讀起來是兩個互相矛盾的斷言。
    ///
    /// 這裡只回答措辭。物件名稱（<see cref="NotificationItem.Subject"/>）與重複次數
    /// （<see cref="NotificationItem.Repeat"/>）怎麼擺是版面：前者接在同一行、後者是徽章，
    /// 都由通知島決定，換一種排法不必回頭改文案。
    /// </remarks>
    public static string Headline(NotificationItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        return item.Status == NotificationStatus.Succeeded
            ? WithElapsedIfSlow(item, item.TitleSource.Done)
            : item.Title;
    }

    /// <summary>
    /// 一列的出處：「哪一份文件 · 哪一個資料庫」。
    /// </summary>
    /// <remarks>
    /// 分隔符號與缺一半時的寫法集中在這裡：通知島與「關於與診斷」的失敗列都要寫這一句，
    /// 兩邊各拼一次就會在同一份資料上出現兩種讀法。
    /// </remarks>
    public static string Provenance(string document, string source)
    {
        document ??= ""; source ??= "";
        if (document.Length == 0) return source;
        return source.Length == 0 ? document : document + Separator + source;
    }

    /// <inheritdoc cref="Provenance(string, string)"/>
    public static string Provenance(NotificationItem item) => item is null
        ? throw new ArgumentNullException(nameof(item))
        : Provenance(item.Document, item.Source);

    /// <summary>狀態列與輔助技術唸出來的那一句。</summary>
    public static string StatusText(NotificationStatus status) => status switch
    {
        NotificationStatus.Running => StatusRunning,
        NotificationStatus.Succeeded => StatusSucceeded,
        NotificationStatus.Degraded => StatusDegraded,
        NotificationStatus.Failed => StatusFailed,
        NotificationStatus.Canceled => StatusCanceled,
        _ => StatusPending,
    };

    /// <summary>
    /// 明細那一行：工作自己回報的訊息，沒有回報時由結果決定的預設措辭。
    /// </summary>
    /// <remarks>執行中沒有訊息就不預留空白列；畫面規則見通知提示文件。</remarks>
    public static string ResultMessage(NotificationItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (!string.IsNullOrWhiteSpace(item.Message)) return item.Message;

        return item.Status switch
        {
            NotificationStatus.Degraded => StatusDegraded,
            NotificationStatus.Failed => ResultFailed,
            NotificationStatus.Canceled => ResultCanceled,
            _ => "",
        };
    }

    private static string WithElapsedIfSlow(NotificationItem item, string headline)
    {
        if (item.Finished is not { } finished) return headline;
        var elapsed = finished - item.Started;
        return elapsed < ElapsedThreshold ? headline : WithElapsed(headline, elapsed.TotalSeconds);
    }
}
