using System;
using System.Globalization;

namespace SqlAssist.Core.Notifications;

/// <summary>
/// 所有通知標題與完成後敘述的唯一出處。
/// </summary>
/// <remarks>
/// 文案契約：<see cref="NotificationItem.Title"/> 是動詞開頭的現在進行式短語、常數字串，
/// 不含物件名稱、不含狀態、不加標點。物件限定名稱放 <see cref="NotificationItem.Subject"/>，
/// 來源文件或資料庫放 <see cref="NotificationItem.Context"/>。
///
/// 常數而不是內插字串，是因為通知在熱路徑上：建議清單每按一次鍵開一次，
/// 中繼資料每一條查詢開一次。組字串的版本即使通知被可見度篩掉也已經配置完畢，
/// 而被篩掉正是預設狀態——高頻的四個種類預設就是關著的。
///
/// 完成後的措辭也集中在這裡：呼叫端只交出三軸與主體，「成功說什麼、降級說什麼、
/// 失敗要不要指去診斷、耗時到幾秒才值得寫出來」全部由這一份決定。分散在呼叫端的版本
/// 會讓同一種結果在畫面上出現好幾種說法，而使用者分不出那是不同的事還是同一件事。
/// </remarks>
public static class NotificationCatalog
{
    /// <summary>耗時到這個門檻才寫進敘述；比這短的數字只是雜訊。</summary>
    private static readonly TimeSpan ElapsedThreshold = TimeSpan.FromSeconds(1);

    /// <summary>完成後轉過去式用的前綴；目錄裡每一個標題都是動詞開頭，接得上。</summary>
    private const string PastTense = "已";

    // ── 中繼資料 ──────────────────────────────────────────────────────────
    public const string LoadingObjects = "載入物件清單";
    public const string LoadingColumns = "載入欄位與定義";
    public const string LoadingIndexes = "載入索引與條件約束";
    public const string LoadingSystemObjects = "載入系統物件";
    public const string LoadingCollations = "載入定序名單";
    public const string LoadingDatabaseCollation = "載入資料庫定序";
    public const string LoadingDatabases = "載入資料庫清單";
    public const string LoadingLinkedServers = "載入連結伺服器";

    /// <summary>跨到同一台伺服器的另一個資料庫；主體是目標資料庫。</summary>
    public const string ConnectingToDatabase = "連線到其他資料庫";

    /// <summary>四段式名稱的遠端跳躍；慢而且一定要讓使用者知道，因此是 Notice。</summary>
    public const string QueryingLinkedServer = "透過連結伺服器查詢";

    /// <summary>只進診斷統計，永不上畫面；等級一律 <see cref="NotificationLevel.Trace"/>。</summary>
    public const string CacheHit = "命中快取";

    // ── 建議清單 ──────────────────────────────────────────────────────────
    public const string PreparingSuggestions = "準備建議清單";
    public const string SortingSuggestions = "排序建議清單";
    public const string FilteringSuggestions = "篩選建議清單";
    public const string LoadingSuggestionDescription = "載入建議說明";
    public const string ResolvingQualifier = "解析限定名稱";
    public const string ShowingSignatureHelp = "顯示函式參數提示";

    // ── 物件提示與結構預覽 ────────────────────────────────────────────────
    public const string PreparingObjectHint = "準備物件提示";
    public const string LoadingStructurePreview = "載入結構預覽";
    public const string RefreshingPreviewSelection = "更新預覽選取";
    public const string GeneratingObjectScript = "產生物件指令碼";
    public const string RunningSchemaAnalysis = "執行結構健檢";

    // ── 分析、編輯與移至定義 ──────────────────────────────────────────────
    public const string AnalyzingBlocks = "分析 T-SQL 區塊";
    public const string ExpandingWildcard = "展開 SELECT ＊";
    public const string ExpandingStatement = "展開語句樣板";
    public const string ExpandingSnippet = "展開程式碼片段";
    public const string GoingToDefinition = "移至定義";
    public const string GeneratingDefinitionScript = "產生定義指令碼";
    public const string OpeningQueryWindow = "開啟新查詢視窗";

    // ── 初始化、連線與設定 ────────────────────────────────────────────────
    public const string InitializingPackage = "初始化 SqlAssist";
    public const string CreatingMetadataConnection = "建立中繼資料連線";
    public const string ReconfirmingConnection = "重新確認連線";
    public const string ReloadingSettings = "重新載入設定";
    public const string RebuildingThemeBrushes = "重建主題筆刷";

    /// <summary>
    /// 畫面上那一列的主要文字：完成後轉過去式並視情況附上耗時。
    /// </summary>
    /// <remarks>
    /// 只有成功才轉過去式。降級、失敗與取消留在現在進行式，因為那一列右邊接著的是
    /// <see cref="StatusText"/>——「已載入欄位與定義 · 失敗」讀起來是兩個互相矛盾的斷言。
    ///
    /// 這裡只回答措辭。物件名稱（<see cref="NotificationItem.Subject"/>）與重複次數
    /// （<see cref="NotificationItem.Repeat"/>）怎麼擺是版面：前者接在同一行、後者是徽章，
    /// 都由通知卡片決定，換一種排法不必回頭改文案。
    /// </remarks>
    public static string Headline(NotificationItem item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        return item.Status == NotificationStatus.Succeeded
            ? PastTense + item.Title + FormatElapsed(item)
            : item.Title;
    }

    /// <summary>狀態列與輔助技術唸出來的那一句。</summary>
    public static string StatusText(NotificationStatus status) => status switch
    {
        NotificationStatus.Running => "執行中",
        NotificationStatus.Succeeded => "已完成",
        NotificationStatus.Degraded => "部分資料無法取得",
        NotificationStatus.Failed => "失敗 · 詳見關於與診斷",
        NotificationStatus.Canceled => "已取消",
        _ => "等待中",
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
            NotificationStatus.Degraded => "部分資料無法取得",
            NotificationStatus.Failed => "請至「關於與診斷」查看通知失敗",
            NotificationStatus.Canceled => "工作已取消",
            _ => "",
        };
    }

    private static string FormatElapsed(NotificationItem item)
    {
        if (item.Finished is not { } finished) return "";
        var elapsed = finished - item.Started;
        return elapsed < ElapsedThreshold
            ? ""
            : "（" + elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " 秒）";
    }
}
