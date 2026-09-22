using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 一筆結果做得到的兩件事（移至定義、在物件總管中選取）——唯一可以辨識酬載型別的地方。
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
internal static partial class SqlSearchActivation
{
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

        // 進場就切，而不是在每個分支裡各補一次：這一支答應「回來時在 UI 執行緒上」，
        // 而已經在上面時 SwitchToMainThreadAsync 是同步完成的，不排訊息也不讓出執行緒。
        // 分支裡各切一次的症狀是加第三個來源時漏掉那一行，而漏掉只在它自己那條路上發作。
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // 這是<b>唯一</b>可以辨識酬載型別的地方，而加一個來源的代價就是這裡多一個分支：
        // 清單樣板、圖示、預覽與命令一個字都沒有跟著改。
        if (hit.ActivatePayload is SqlAgentJobSearchTarget job)
        {
            // 明寫 true 而不是留空：這一支答應回來時在 UI 執行緒上，而整個專案滿是
            // ConfigureAwait(false)，不寫的那一個看起來像漏掉的。已經在 UI 執行緒上時
            // 續程原地跑，不多一次派送。
            return await ActivateJobAsync(job, services, catalogs).ConfigureAwait(true);
        }

        if (hit.ActivatePayload is not SqlCatalogSearchTarget target)
        {
            return "這一筆沒有可以開啟的定義。";
        }

        // 新視窗沿用的是查詢視窗那一條連線。這一筆來自別台伺服器時，那份定義會落在一個連著
        // 另一台伺服器的視窗裡——使用者在那裡按 F5 就是對錯的伺服器執行。
        // 這一步<b>不</b>悄悄照做：右邊的預覽已經讀得到完整定義，而開錯視窗看不出差別。
        // 問的是「是不是同一台」而不是「有沒有指名」：指名的那一台常常正是查詢視窗連著的
        // 那一台，用後者代答就是把使用者擋在他自己已經連好的伺服器外面。
        if (!catalogs.SharesActiveEditorServer())
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

        var objectInfo = ToObjectInfo(target);
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
    /// 把物件總管展開到這一筆指的節點並選取它。
    /// </summary>
    /// <returns>
    /// 成功時為 null，否則是要顯示在工具窗頁尾的那一句；<b>一律在 UI 執行緒上完成</b>。
    /// </returns>
    /// <remarks>
    /// 執行緒分三段：UI 執行緒挑伺服器與目錄，背景問父物件是誰，回到 UI 執行緒逐一導航。
    /// 中間那一段由 <c>GetParentAsync</c> 自己讓出執行緒，這一層不再包一次 <c>Task.Run</c>。
    ///
    /// 這一條與<see cref="ActivateAsync">移至定義</see>互補，所以<b>沒有</b>那一道
    /// 「只沿用得到查詢視窗那條連線」的守門：伺服器由樹上那一台決定，指名別台時正好是
    /// 這一顆還能用。兩顆都擋掉的話，指名伺服器之後一列結果什麼都做不了。
    ///
    /// 候選由 <see cref="SqlObjectExplorerUrn"/> 排好，這裡<b>依序</b>試到第一個指得到的為止，
    /// 並且說出停在哪一層。試到第二個就默默當成成功的話，使用者會以為自己正看著那個條件約束，
    /// 而選取的其實是它所屬的資料表。
    /// </remarks>
    public static async Task<string?> SelectInExplorerAsync(
        SearchHit hit, IServiceProvider services, SqlSearchCatalogs catalogs)
    {
        if (hit is null) throw new ArgumentNullException(nameof(hit));
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (catalogs is null) throw new ArgumentNullException(nameof(catalogs));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (catalogs.ResolveExplorerServer() is not { } server)
        {
            // 兩句的下一步不同：一句是「去物件總管連上那台」，另一句是「先連上查詢視窗」。
            return catalogs.ActiveEditorServerName() is { Length: > 0 } name
                ? $"物件總管上沒有連到 {name} 的連線；在那裡連上這一台之後再按一次。"
                : "目前的查詢視窗沒有連線，說不出要在物件總管的哪一台上找。";
        }

        // 接下來每一步都碰 UI（導覽服務、頁尾那幾句），所以 true。這裡曾經是 false，
        // 而症狀只在條件約束與觸發程序上出現——也只有那兩種真的 await 過一趟查詢，
        // 其餘種類同步完成、續程原地跑，看起來完全正常。
        var nodes = await ResolveNodesAsync(hit, server.RootUrn, catalogs).ConfigureAwait(true);

        if (nodes.Count == 0)
        {
            return hit.ActivatePayload is SqlCatalogSearchTarget missing &&
                SqlObjectExplorerUrn.RequiresParent(missing.Kind)
                ? $"問不到 {missing.Name} 掛在哪一個物件上，可能是連線已中斷或它已經卸除。"
                : "這一筆在物件總管上指不到節點。";
        }

        using var notification = NotificationCenter.Default.Begin(
            NotificationCatalog.SelectingInObjectExplorer,
            NotificationKind.Navigation,
            NotificationOrigin.User,
            NotificationLevel.Info,
            hit.Title,
            // 出處是樹上那一台伺服器，不是一份文件：這條路徑沒有開任何查詢視窗。
            source: server.DisplayName);

        for (var index = 0; index < nodes.Count; index++)
        {
            // 不給取消權杖：使用者按的是「帶我過去」，中途放掉等於按了沒反應。
            // 帶上 OwnerUrn，導航才知道這一個是不是畫在別人底下的——那幾種指不到樹根，
            // 要先到父物件再往下找。哪一種畫在誰底下只有 SqlObjectExplorerUrn 知道，
            // 這一層照欄位走，不自己判斷種類。
            var node = nodes[index];

            if (!await SsmsObjectExplorer.TryNavigateAsync(
                    services, node.Urn, node.OwnerUrn, CancellationToken.None))
            {
                continue;
            }

            return index == 0
                ? null
                : $"物件總管上找不到{Describe(nodes[0])}，已改為選取{Describe(nodes[index])}。";
        }

        notification.Fail();

        // 連最寬鬆的那一個都指不到：兩種來源的下一步完全不同。
        return hit.ActivatePayload is SqlAgentJobSearchTarget job
            ? $"物件總管上找不到作業 {job.JobName}：它可能已經刪除，或這個登入看不到 SQL Server Agent。"
            : $"物件總管上找不到 {hit.Title}：它可能已經卸除，或被物件總管的篩選器擋掉了；" +
                "重新整理那個資料夾之後再試一次。";
    }

    /// <summary>
    /// 這一筆在樹上的候選節點，由精確到寬鬆。
    /// </summary>
    /// <remarks>
    /// 三條路：作業自己一條；觸發程序與條件約束要先問一趟目錄，因為它們畫在父物件底下，
    /// 而一筆結果身上只有自己的 <c>object_id</c>；其餘照資料行命中與否分。
    ///
    /// 問父物件走的是<b>一條查詢</b>（<c>GetParentAsync</c>），不載入父物件的結構：
    /// 跳到一個條件約束不需要知道那張表的索引與外來鍵長什麼樣子。那一趟自己在背景跑
    /// （<c>GetParentAsync</c> 內部就是 <c>Task.Run</c>），所以這裡直接 await：
    /// 再包一層只是多排一次工作，UI 執行緒一樣不會停在查詢上。
    ///
    /// 組位址是純字串，留在哪一條執行緒上都不影響畫面；回來時在哪一條由呼叫端的
    /// <c>await</c> 決定，這一支<b>不</b>替呼叫端切回去。恢復執行緒是階段邊界的事，
    /// 不是資料解析函式的事——寫在這裡的話，呼叫端一個 <c>ConfigureAwait(false)</c>
    /// 就能把它作廢，而看起來像是這一支失了信。真正擋住那一種錯的是
    /// <c>SsmsObjectExplorer.TryNavigateAsync</c> 自己切。
    /// </remarks>
    private static async Task<IReadOnlyList<SqlExplorerNode>> ResolveNodesAsync(
        SearchHit hit, string rootUrn, SqlSearchCatalogs catalogs)
    {
        // 目錄要在 UI 執行緒上問（SqlSearchCatalogs 的解析都有 assert）。呼叫端已經切過，
        // 這一步就是同步完成；自己切一次是為了讓這一支從任何地方叫都成立。
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // 這是唯一可以辨識酬載型別的地方，與啟動那一支同一條紅線。
        if (hit.ActivatePayload is SqlAgentJobSearchTarget job)
        {
            return SqlObjectExplorerUrn.ForJob(rootUrn, job.JobName);
        }

        if (hit.ActivatePayload is not SqlCatalogSearchTarget target) return Array.Empty<SqlExplorerNode>();

        if (SqlObjectExplorerUrn.RequiresParent(target.Kind))
        {
            var child = ToObjectInfo(target);

            if (catalogs.ResolveFor(child) is not { } catalog) return Array.Empty<SqlExplorerNode>();

            var parent = await catalog
                .GetParentAsync(child, CancellationToken.None, NotificationOrigin.User)
                .ConfigureAwait(false);

            return parent is null
                ? Array.Empty<SqlExplorerNode>()
                : SqlObjectExplorerUrn.ForChild(rootUrn, parent, target.Name);
        }

        return target.ColumnName is { Length: > 0 } column
            ? SqlObjectExplorerUrn.ForColumn(
                rootUrn, target.DatabaseName, target.SchemaName, target.Name, target.Kind, column)
            : SqlObjectExplorerUrn.ForObject(
                rootUrn, target.DatabaseName, target.SchemaName, target.Name, target.Kind);
    }

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

        // 與目錄那一條同一道守門，連判斷都同一支：這一筆來自別台伺服器時，那份步驟命令會
        // 落在一個連著另一台伺服器的視窗裡，而使用者在那裡按 F5 就是對錯的伺服器執行——
        // 一段作業步驟通常正是會改資料的那種 SQL。
        if (!catalogs.SharesActiveEditorServer())
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
