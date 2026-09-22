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
    /// 描述啟動之後會發生什麼；給 Tooltip、右鍵選單與自動化名稱用。
    /// </summary>
    public static string Describe(SearchHit? hit)
    {
        if (hit?.ActivatePayload is SqlAgentJobSearchTarget job) return DescribeJob(job);

        if (hit?.ActivatePayload is not SqlCatalogSearchTarget target) return "";

        // 資料行命中只是「這個物件的哪一行對上了」，導航目標仍然是那個物件本身；
        // 寫成「捲到資料行」會承諾一件這條路徑沒有做的事。
        return target.ColumnName is { Length: > 0 } column
            ? "在新查詢視窗開啟 " + target.DatabaseName + " 的 " + target.Name + " 定義（命中資料行 " + column + "）"
            : "在新查詢視窗開啟 " + target.DatabaseName + " 的 " + target.Name + " 定義";
    }

    /// <summary>
    /// 這一筆在物件總管上指得出節點嗎；指不出來時 UI 收起入口。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="CanActivate"/> 是<b>兩個</b>問題，兩邊都有對方沒有的情形：指名了別台
    /// 伺服器時，定義開不進沿用連線的新查詢視窗，樹上那一台卻正好在。共用一個判斷的症狀是
    /// 其中一顆入口永遠跟著另一顆一起消失。
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
    private static string DescribeJob(SqlAgentJobSearchTarget job) =>
        job.StepId is { } step
            ? $"在新查詢視窗開啟 {job.ServerName} 上 {job.JobName} 第 {step} 步的命令"
            : $"在新查詢視窗開啟 {job.ServerName} 上 {job.JobName} 的所有步驟命令";

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
