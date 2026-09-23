using System;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 一筆結果<b>是什麼、做得到什麼</b>——辨識酬載型別的那一半，不碰任何宿主服務。
/// </summary>
/// <remarks>
/// 與 <c>SqlSearchActivation.cs</c> 是同一個類別的兩半，切開只有一個理由：這一半零
/// VS／SSMS 相依，清單列（<see cref="SqlSearchRow"/>）與 WPF 樣板的回歸測試才編得進來，
/// 而那些測試不載入 SSMS 的組件。紅線沒有變寬——辨識
/// <see cref="SearchHit.ActivatePayload"/> 仍然只准發生在這個類別裡，清單、預覽與命令
/// 一律只讀 <see cref="SearchHit"/> 的欄位。
/// </remarks>
internal static partial class SqlSearchActivation
{
    /// <summary>
    /// 這一筆有沒有東西可以啟動；沒有的話 UI 收起啟動入口，而不是留一顆按了沒反應的按鈕。
    /// </summary>
    public static bool CanActivate(SearchHit? hit) =>
        hit?.ActivatePayload is SqlCatalogSearchTarget or SqlAgentJobSearchTarget;

    /// <summary>
    /// 這一筆是在哪一台伺服器上搜到的；不是伺服器上的東西時為 null。
    /// </summary>
    /// <remarks>
    /// 認的是 <see cref="ISqlSearchTarget"/> 而不是逐一列出目錄物件與作業：新來源的酬載只要
    /// 實作它，「在樹上找哪一台」「能不能沿用查詢視窗」「目錄是不是同一台」三道判斷就一起套上。
    /// </remarks>
    public static SqlSearchOrigin? OriginOf(SearchHit? hit) =>
        hit?.ActivatePayload is ISqlSearchTarget target ? target.Origin : null;

    /// <summary>
    /// 預覽讀得到完整定義的那個目錄物件；只提供片段的來源（作業、之後的 SQL Memory）為 null。
    /// </summary>
    public static SqlCatalogSearchTarget? DefinitionOf(SearchHit? hit) =>
        hit?.ActivatePayload as SqlCatalogSearchTarget;

    /// <summary>
    /// 啟動這一筆會開出<b>沒有連線</b>的查詢視窗：它不在作用中查詢視窗那一台上。
    /// </summary>
    /// <param name="activeEditorServer">作用中查詢視窗連著的伺服器；沒有視窗或沒有連線時為 null。</param>
    /// <remarks>
    /// 新視窗沿用得到的只有作用中查詢視窗那條連線，而這一筆來自別台時沿用它就是在錯的伺服器上
    /// 按 F5。所以那一種改開未連線的視窗，由 SSMS 在第一次執行時要求連線。沒有查詢視窗時
    /// 同樣是未連線：沒有連線可以沿用，而不是做不到。
    ///
    /// 比對走 <see cref="SqlSearchCatalogs.IsSameServer(string?, string?)"/>，與執行當下
    /// （<c>SharesActiveEditorServer</c>）同一份規則；兩邊各比一次的症狀是列上說會開未連線的
    /// 視窗，開出來的卻連著查詢視窗那一台。
    /// </remarks>
    public static bool OpensUnconnected(SearchHit? hit, string? activeEditorServer) =>
        CanActivate(hit) &&
        OriginOf(hit) is { } origin &&
        !SqlSearchCatalogs.IsSameServer(origin.ServerName, activeEditorServer);

    /// <summary>右鍵選單與停駐那一顆的名稱；未連線時在名稱上就說，不等使用者去讀提示。</summary>
    public static string ActivateLabel(bool unconnected) => unconnected ? "移至定義（未連線）" : "移至定義";

    /// <summary>
    /// 描述啟動之後會發生什麼；給 Tooltip、右鍵選單與自動化名稱用。
    /// </summary>
    /// <param name="unconnected">
    /// 會開未連線的視窗（<see cref="OpensUnconnected"/>）；那一種在按下去之前就說出來自哪一台，
    /// 事後才說的話，使用者已經對著一個不知道連到哪裡的視窗了。
    /// </param>
    public static string Describe(SearchHit? hit, bool unconnected = false)
    {
        var action = hit?.ActivatePayload switch
        {
            SqlAgentJobSearchTarget job => DescribeJob(job, unconnected),
            // 資料行命中只是「這個物件的哪一行對上了」，導航目標仍然是那個物件本身；
            // 寫成「捲到資料行」會承諾一件這條路徑沒有做的事。
            SqlCatalogSearchTarget { ColumnName: { Length: > 0 } column } target =>
                "在" + WindowNoun(unconnected) + "開啟 " + target.DatabaseName + " 的 " + target.Name +
                " 定義（命中資料行 " + column + "）",
            SqlCatalogSearchTarget target =>
                "在" + WindowNoun(unconnected) + "開啟 " + target.DatabaseName + " 的 " + target.Name + " 定義",
            _ => ""
        };

        return action.Length > 0 && unconnected && OriginOf(hit) is { } origin
            ? action + "。它在 " + origin + " 上，不在目前查詢視窗那一台；按 F5 時 SSMS 會要求連線。"
            : action;
    }

    /// <summary>成功之後頁尾那一句；說得出視窗有沒有連線，使用者按 F5 之前就知道會跳連線對話框。</summary>
    public static string DescribeOpened(SearchHit? hit, bool unconnected)
    {
        if (hit is null) return "";

        var opened = "已在" + WindowNoun(unconnected) + "開啟 " + hit.Title + " 的" + SubjectNoun(hit);

        return unconnected && OriginOf(hit) is { } origin
            ? opened + "；按 F5 時請連到 " + origin + "。"
            : opened + "。";
    }

    /// <summary>
    /// 未連線視窗開頭那幾行註解：來源伺服器與資料庫；不是伺服器上的東西時為空字串。
    /// </summary>
    /// <remarks>
    /// 視窗沒有連線，標題列與資料庫下拉都說不出這份東西從哪裡來，而 SSMS 的連線對話框預設
    /// 帶的是上一次那一台。寫在指令碼裡是唯一跟著這份文字走的地方——另存成檔案、
    /// 過幾天再打開，它還說得出該連到哪裡。伺服器寫的是搜到它的那條連線上的名稱
    /// （<see cref="SqlSearchOrigin"/>），就是連線對話框要填的那一個。
    /// </remarks>
    public static string UnconnectedHeader(SearchHit? hit, string newLine)
    {
        if (newLine is null) throw new ArgumentNullException(nameof(newLine));

        var (what, database) = hit?.ActivatePayload switch
        {
            SqlCatalogSearchTarget target => ("定義來自 " + target.Origin + " 上的 " + target.DatabaseName + " 資料庫",
                target.DatabaseName),
            SqlAgentJobSearchTarget { StepId: { } step } job =>
                ($"命令來自 {job.Origin} 上的作業 {job.JobName} 第 {step} 步", job.DatabaseName),
            SqlAgentJobSearchTarget job => ("命令來自 " + job.Origin + " 上的作業 " + job.JobName, job.DatabaseName),
            _ => ("", "")
        };

        if (what.Length == 0 || OriginOf(hit) is not { } origin) return "";

        var connect = database.Length == 0
            ? "-- 按 F5 時 SSMS 會要求連線：請連到 " + origin + " 再執行。"
            : "-- 按 F5 時 SSMS 會要求連線：請連到 " + origin + "，並把資料庫切到 " + database + " 再執行。";

        return "-- 這個查詢視窗沒有連線。" + what + "。" + newLine + connect + newLine + newLine;
    }

    private static string WindowNoun(bool unconnected) => unconnected ? "未連線的新查詢視窗" : "新查詢視窗";

    /// <summary>
    /// 這一筆在物件總管上指得出節點嗎；指不出來時 UI 收起入口。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="CanActivate"/> 是<b>兩個</b>問題，兩邊都有對方沒有的情形：一份作業或資料表
    /// 開得出指令碼，一個沒有節點種類的物件卻指不到樹上。共用一個判斷的症狀是其中一顆入口
    /// 永遠跟著另一顆一起變灰。
    ///
    /// 觸發程序與條件約束<b>算數</b>：它們畫在父物件底下，位址要先問一趟目錄
    /// （<see cref="SqlObjectExplorerUrn.RequiresParent"/>），但樹上確實有那個節點。
    /// 這一步不問資料庫，所以答的是「這一種東西有沒有節點」，不是「這一個還在不在」。
    /// </remarks>
    public static bool CanSelectInExplorer(SearchHit? hit) => hit?.ActivatePayload switch
    {
        SqlCatalogSearchTarget target =>
            SqlObjectExplorerUrn.Supports(target.Kind) || SqlObjectExplorerUrn.RequiresParent(target.Kind),
        SqlAgentJobSearchTarget => true,
        _ => false
    };

    /// <summary>描述這一次會選到哪裡；給 Tooltip 與右鍵選單用。</summary>
    /// <remarks>
    /// 資料行命中說得出資料行：樹上就有那一行，而這條路徑真的會停在它身上——
    /// 與<see cref="Describe">移至定義</see>那一支剛好相反，那一條的目標永遠是整個物件。
    /// 步驟只說得到作業為止：<c>JobServer</c> 底下沒有步驟節點。
    /// </remarks>
    public static string DescribeSelectInExplorer(SearchHit? hit) => hit?.ActivatePayload switch
    {
        SqlAgentJobSearchTarget job => "在物件總管中選取 " + job.ServerName + " 上的作業 " + job.JobName,
        SqlCatalogSearchTarget target when target.ColumnName is { Length: > 0 } column =>
            "在物件總管中選取 " + target.DatabaseName + " 的 " + target.Name + "，並停在資料行 " + column + " 上",
        SqlCatalogSearchTarget target when CanSelectInExplorer(hit) =>
            "在物件總管中選取 " + target.DatabaseName + " 的 " + target.Name,
        _ => ""
    };

    /// <summary>降級那一句裡的那個名詞；節點種類的措辭只有這一份。</summary>
    private static string Describe(SqlExplorerNode node) => node.Kind switch
    {
        SqlExplorerNodeKind.Column => "資料行 " + node.Name,
        SqlExplorerNodeKind.Trigger => "觸發程序 " + node.Name,
        SqlExplorerNodeKind.Constraint => "條件約束 " + node.Name,
        SqlExplorerNodeKind.Job => "作業 " + node.Name,
        _ => node.Name
    };

    /// <summary>
    /// 一筆結果換成物件明細。
    /// </summary>
    /// <remarks>
    /// <see cref="SqlCatalogSearchTarget.DatabaseName"/> 一定要帶上：<c>object_id</c> 只在
    /// 那個資料庫裡唯一，下游要先換目錄再用編號。兩條路徑共用這一份，各組一次的症狀是
    /// 其中一條忘了帶資料庫，而它問到的是另一份目錄裡剛好同號的物件。
    /// </remarks>
    private static SqlObjectInfo ToObjectInfo(SqlCatalogSearchTarget target) => new(
        target.ObjectId,
        target.SchemaName,
        target.Name,
        target.Kind,
        target.DatabaseName);

    /// <summary>作業沒有「定義」可開；主要動作是把步驟命令開進新查詢視窗。</summary>
    private static string DescribeJob(SqlAgentJobSearchTarget job, bool unconnected) =>
        job.StepId is { } step
            ? $"在{WindowNoun(unconnected)}開啟 {job.ServerName} 上 {job.JobName} 第 {step} 步的命令"
            : $"在{WindowNoun(unconnected)}開啟 {job.ServerName} 上 {job.JobName} 的所有步驟命令";

    /// <summary>
    /// 這一筆開出來的東西叫什麼；工具窗的進行中與完成訊息共用同一個詞。
    /// </summary>
    /// <remarks>
    /// 交出一個名詞而不是整句，是為了讓辨識酬載型別仍然只發生在這一支：工具窗要組
    /// 「正在取得 X 的○○…」與「已在新查詢視窗開啟 X 的○○。」兩句，自己 <c>is</c>
    /// 一次型別就破了那條紅線。寫死成「定義」的症狀是作業那幾列說「已開啟…的定義」，
    /// 而作業根本沒有定義。
    /// </remarks>
    public static string SubjectNoun(SearchHit? hit) =>
        hit?.ActivatePayload is SqlAgentJobSearchTarget ? "命令" : "定義";
}
