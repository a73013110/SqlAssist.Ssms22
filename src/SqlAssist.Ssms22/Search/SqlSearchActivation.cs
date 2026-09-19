using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Formatting;
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

        // 這是<b>唯一</b>可以辨識酬載型別的地方，而加一個來源的代價就是這裡多一個分支：
        // 清單樣板、圖示、預覽與命令一個字都沒有跟著改。
        if (hit.ActivatePayload is SqlAgentJobSearchTarget job)
        {
            return await ActivateJobAsync(job, services, catalogs).ConfigureAwait(false);
        }

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

    /// <summary>作業沒有「定義」可開；主要動作是把步驟命令開進新查詢視窗。</summary>
    private static string DescribeJob(SqlAgentJobSearchTarget job) =>
        job.StepId is { } step
            ? $"在新查詢視窗開啟 {job.ServerName} 上 {job.JobName} 第 {step} 步的命令"
            : $"在新查詢視窗開啟 {job.ServerName} 上 {job.JobName} 的所有步驟命令";

    /// <summary>
    /// 啟動一筆作業結果：把它的步驟命令開進一個沿用目前連線的新查詢視窗。
    /// </summary>
    /// <remarks>
    /// <b>為什麼是「開進查詢視窗」而不是「複製作業名稱」。</b>清單上同時有目錄物件與作業，
    /// 而雙擊在兩種列上必須是同一件事——一種開視窗、另一種複製字串的話，
    /// 使用者每一次都要先看清楚自己選到哪一種。步驟命令又剛好是這個來源身上唯一
    /// 「打開來看得到東西」的部分：排程、通知與歷程都在 SSMS 自己的作業屬性對話框裡，
    /// 而那個對話框我們打不開（它是物件總管的一條私有路徑）。
    ///
    /// 命令本文在這裡才向 <c>msdb</c> 要，不跟著酬載走：一輪結果上限一百筆，
    /// 每一筆都帶著幾千行的話，整份清單會被釘在記憶體裡；順帶換到的是
    /// 「開出來的是現在那一版」。
    ///
    /// 執行緒分工與目錄那一條完全相同：UI 執行緒只解析服務，查詢與組字串在背景，
    /// 開窗與寫入回到 UI 執行緒。
    /// </remarks>
    private static async Task<string?> ActivateJobAsync(
        SqlAgentJobSearchTarget job, IServiceProvider services, SqlSearchCatalogs catalogs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // 與目錄那一條同一道守門：新視窗沿用的是查詢視窗那條連線。指名了別台伺服器時，
        // 那份步驟命令會落在一個連著另一台伺服器的視窗裡，而使用者在那裡按 F5
        // 就是對錯的伺服器執行——一段作業步驟通常正是會改資料的那種 SQL。
        if (!catalogs.FollowsActiveEditor)
        {
            return $"這一筆在 {job.ServerName} 上。新查詢視窗只沿用得到目前查詢視窗那條連線，" +
                "請先把查詢視窗連到那一台，或直接看右邊的命令片段。";
        }

        var view = ActiveSqlEditor.Current;

        if (view is null)
        {
            return "請先開啟一個已連線的 SQL 查詢視窗，新視窗才有連線可以沿用。";
        }

        // 連線一律向 SqlSearchCatalogs 要，而且只借不留：所有權在
        // SqlMetadataCatalogRegistry，留一份的症狀是換過連線之後每一次啟動都以
        // ObjectDisposedException 收場，而那不是 DbException，降級接不住。
        if (catalogs.Resolve() is not { } catalog)
        {
            return "取不到目前查詢視窗的連線，請先連上伺服器。";
        }

        var documentName = ActiveSqlEditor.GetDocumentName(view);
        var subject = job.StepId is { } step
            ? job.JobName + " 第 " + step.ToString(CultureInfo.InvariantCulture) + " 步"
            : job.JobName;

        using var notification = NotificationCenter.Default.Begin(
            NotificationCatalog.GoingToDefinition,
            NotificationKind.Navigation,
            NotificationOrigin.User,
            NotificationLevel.Info,
            subject,
            documentName);

        var script = await Task
            .Run(() => SqlAgentJobScript.TryBuild(
                catalog.ConnectionSource, job, Environment.NewLine, CancellationToken.None))
            .ConfigureAwait(false);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (script is null)
        {
            notification.Fail();

            // 三個原因都要寫出來：它們的下一步完全不同（去看作業還在不在、去要 msdb 權限、
            // 去看連線），而只說「取不到」的話使用者查不出該去看哪一個。
            return $"在 {job.ServerName} 取不到 {subject} 的步驟命令：作業可能已經刪除、" +
                "這個登入對 msdb 沒有權限，或連線已中斷。";
        }

        var failure = SqlDefinitionScript.WriteToNewWindow(
            services,
            new SqlObjectScriptText(script, 0),
            subject,
            $"已在新查詢視窗開啟 {subject} 的命令",
            documentName);

        if (failure is not null) notification.Fail();

        return failure;
    }
}
