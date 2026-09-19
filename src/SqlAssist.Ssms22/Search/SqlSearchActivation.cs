using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 啟動一筆結果（移至定義）——唯一可以辨識酬載型別的地方。
/// </summary>
/// <remarks>
/// 清單、預覽與狀態列一律只讀 <see cref="SearchHit"/> 的欄位；只有這一步需要知道那一筆
/// 到底是什麼東西。這是<b>刻意留下的擴充接縫</b>：之後加 SQL Memory、開啟中的查詢分頁或
/// 片段 provider 時，只在這一支多一個 <c>is</c> 分支，清單樣板、預覽與命令都不必跟著改。
/// 任何一處 UI 自己向下轉型 <see cref="SearchHit.ActivatePayload"/> 都會把這個代價
/// 從「多一個分支」變回「改整份樣板」。
///
/// 目錄物件接的是 F12 那一條既有路徑，不另建第二條：目錄向
/// <see cref="SqlSearchCatalogs"/> 要（與清單、預覽同一份），
/// <c>SqlMetadataCatalog.GetStructureAsync</c> 取結構，
/// <see cref="SqlDefinitionScript"/> 組指令碼並開進新的查詢視窗。
///
/// <b>沒有接上結構預覽。</b>浮動結構預覽掛在編輯器自己的空間保留管理員上，
/// <c>SqlStructurePreview.ShowAt</c> 要的是一個 <c>ITrackingSpan</c>——也就是某份 SQL 文字
/// 裡的一段。工具窗的一列結果沒有那個東西，硬接只能拿目前查詢視窗的游標當錨點，
/// 那會把預覽畫在一個與這一筆結果無關的位置上。右鍵選單因此提供「複製限定名稱」，
/// 讓使用者自己把名稱貼回查詢視窗，再用既有的 Ctrl+F12。
/// </remarks>
internal static class SqlSearchActivation
{
    /// <summary>
    /// 這一筆有沒有東西可以啟動；沒有的話 UI 收起啟動入口，而不是留一顆按了沒反應的按鈕。
    /// </summary>
    public static bool CanActivate(SearchHit? hit) => hit?.ActivatePayload is SqlCatalogSearchTarget;

    /// <summary>
    /// 描述啟動之後會發生什麼；給 Tooltip、右鍵選單與自動化名稱用。
    /// </summary>
    public static string Describe(SearchHit? hit)
    {
        if (hit?.ActivatePayload is not SqlCatalogSearchTarget target) return "";

        // 資料行命中只是「這個物件的哪一行對上了」，導航目標仍然是那個物件本身；
        // 寫成「捲到資料行」會承諾一件這條路徑沒有做的事。
        return target.ColumnName is { Length: > 0 } column
            ? "在新查詢視窗開啟 " + target.DatabaseName + " 的 " + target.Name + " 定義（命中資料行 " + column + "）"
            : "在新查詢視窗開啟 " + target.DatabaseName + " 的 " + target.Name + " 定義";
    }

    /// <summary>
    /// 啟動一筆結果：把它的定義開進一個沿用目前連線的新查詢視窗。
    /// </summary>
    /// <returns>
    /// 成功時為 null，否則是要顯示在工具窗頁尾的那一句；<b>一律在 UI 執行緒上完成</b>，
    /// 呼叫端接到之後可以直接寫進畫面。
    /// </returns>
    /// <remarks>
    /// 執行緒分工與 F12 同一套：UI 執行緒只解析服務，查詢與組指令碼在背景，
    /// 開窗與寫入回到 UI 執行緒。使用者是自己雙擊的，因此這條路徑<b>不</b>走
    /// <c>SqlAssistPlatformGuard</c> 的收斂——安靜地什麼都不做等於故障，
    /// 每一種失敗都要說得出原因。
    ///
    /// 查不到就說查不到：<b>禁止</b>退回拿目前連線裡同名的物件回答。物件自己記著
    /// <see cref="SqlCatalogSearchTarget.DatabaseName"/>，<c>object_id</c> 只在那個資料庫裡
    /// 唯一，所以先組出帶資料庫的 <see cref="SqlObjectInfo"/>，再讓中繼資料層換目錄。
    /// </remarks>
    public static async Task<string?> ActivateAsync(
        SearchHit hit, IServiceProvider services, SqlSearchCatalogs catalogs)
    {
        if (hit is null) throw new ArgumentNullException(nameof(hit));
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (catalogs is null) throw new ArgumentNullException(nameof(catalogs));

        if (hit.ActivatePayload is not SqlCatalogSearchTarget target)
        {
            return "這一筆沒有可以開啟的定義。";
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // 新視窗沿用的是查詢視窗那一條連線。指名了別台伺服器時，那份定義會落在一個連著
        // 另一台伺服器的視窗裡——使用者在那裡按 F5 就是對錯的伺服器執行。
        // 這一步<b>不</b>悄悄照做：右邊的預覽已經讀得到完整定義，而開錯視窗看不出差別。
        if (!catalogs.FollowsActiveEditor)
        {
            return $"這一筆在 {catalogs.Server?.DisplayName} 上。新查詢視窗只沿用得到目前查詢視窗那條連線，" +
                "請先把查詢視窗連到那一台，或直接看右邊的定義預覽。";
        }

        // 沒有查詢視窗就沒有連線可沿用，而 SSMS 的新查詢視窗一定要帶著一組連線資訊才開得起來。
        var view = ActiveSqlEditor.Current;

        if (view is null)
        {
            return "請先開啟一個已連線的 SQL 查詢視窗，新視窗才有連線可以沿用。";
        }

        var objectInfo = new SqlObjectInfo(
            target.ObjectId,
            target.SchemaName,
            target.Name,
            target.Kind,
            target.DatabaseName);
        var documentName = ActiveSqlEditor.GetDocumentName(view);

        // 取結構與預覽走同一份目錄（同一個 SqlSearchCatalogs），不另問中繼資料服務：
        // 兩邊各問一次的症狀是預覽與新視窗的內容來自不同的地方。
        if (catalogs.ResolveFor(objectInfo) is not { } catalog)
        {
            return $"在 {target.DatabaseName} 取不到 {objectInfo.QualifiedName} 的結構，可能是連線已中斷或權限不足。";
        }

        using var notification = NotificationCenter.Default.Begin(
            NotificationCatalog.GoingToDefinition,
            NotificationKind.Navigation,
            NotificationOrigin.User,
            NotificationLevel.Info,
            objectInfo.QualifiedName,
            documentName);

        // Task.Run 而不是直接 await：GetStructureAsync 在第一個 await 之前是同步跑的，
        // 留在 UI 執行緒上就是第四層查詢的準備工作卡住畫面。
        var structure = await Task
            .Run(() => catalog.GetStructureAsync(objectInfo, CancellationToken.None, NotificationOrigin.User))
            .ConfigureAwait(false);

        if (structure is null)
        {
            notification.Fail();
            // 回到 UI 執行緒再交還：呼叫端拿這一句去寫工具窗的頁尾。
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return $"在 {target.DatabaseName} 取不到 {objectInfo.QualifiedName} 的結構，可能是連線已中斷或權限不足。";
        }

        var script = await Task.Run(() => SqlDefinitionScript.Build(structure, documentName)).ConfigureAwait(false);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var failure = SqlDefinitionScript.WriteToNewWindow(services, script, objectInfo, documentName);

        if (failure is not null) notification.Fail();

        return failure;
    }
}
