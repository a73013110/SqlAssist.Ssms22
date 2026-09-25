using System;
using System.Collections.Generic;
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
/// 清單與預覽一律只讀 <see cref="SearchHit"/> 的欄位；只有這一步需要知道那一筆
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
    /// 啟動一筆結果：把它的定義開進新查詢視窗——同一台就沿用目前連線，別台就不連線。
    /// </summary>
    /// <remarks>
    /// <b>結果一律由這裡送通知</b>，呼叫端不必再說一次：進行中、成功（未連線時附上該連哪一台，
    /// 見 <see cref="OpenedNote"/>）與每一種失敗都在「移至定義」那一則上。還沒開始取結構就被擋下的
    /// 那幾種（查不到目錄、範圍換到別台）也走同一個標題，使用者看到的是同一件事的結果。
    /// <b>一律在 UI 執行緒上完成</b>。
    ///
    /// 執行緒分工與 F12 同一套：UI 執行緒只解析服務，查詢與組指令碼在背景，
    /// 開窗與寫入回到 UI 執行緒。使用者是自己雙擊的，因此這條路徑<b>不</b>走
    /// <c>SqlAssistPlatformGuard</c> 的收斂——安靜地什麼都不做等於故障，
    /// 每一種失敗都要說得出原因。
    ///
    /// 查不到就說查不到：<b>禁止</b>退回拿目前連線裡同名的物件回答。物件自己記著
    /// <see cref="SqlCatalogSearchTarget.DatabaseName"/>，<c>object_id</c> 只在那個資料庫裡
    /// 唯一，所以先組出帶資料庫的 <see cref="SqlObjectInfo"/>，再讓中繼資料層換目錄。
    /// </remarks>
    public static async Task ActivateAsync(SearchHit hit, IServiceProvider services, SqlSearchCatalogs catalogs)
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
            await ActivateJobAsync(hit, job, services, catalogs).ConfigureAwait(true);
            return;
        }

        if (hit.ActivatePayload is not SqlCatalogSearchTarget target)
        {
            Reject(NotificationCatalog.GoingToDefinition, hit.Title, SqlSearchText.NoDefinitionToOpen);
            return;
        }

        var objectInfo = ToObjectInfo(target);
        var missing = SqlSearchText.StructureMissing(target.DatabaseName, objectInfo.QualifiedName);

        // 取結構與預覽走同一份目錄（同一個 SqlSearchCatalogs），不另問中繼資料服務：
        // 兩邊各問一次的症狀是預覽與新視窗的內容來自不同的地方。先問這一步，是因為範圍已經
        // 換到別台時，下面那兩句（去開查詢視窗、去看預覽）都給錯了下一步——該做的是重新搜尋。
        var catalog = catalogs.ResolveFor(objectInfo, target.Origin, out var elsewhere);

        if (elsewhere || catalog is null)
        {
            Reject(NotificationCatalog.GoingToDefinition, objectInfo.QualifiedName,
                elsewhere ? SqlSearchCatalogs.ElsewhereNotice(target.Origin) : missing);
            return;
        }

        var (unconnected, documentName) = ChooseWindow(catalogs, target.Origin);

        using var notification = BeginGoingToDefinition(objectInfo.QualifiedName, documentName, target.Origin, unconnected);

        // Task.Run 而不是直接 await：GetStructureAsync 在第一個 await 之前是同步跑的，
        // 留在 UI 執行緒上就是第四層查詢的準備工作卡住畫面。
        var structure = await Task
            .Run(() => catalog.GetStructureAsync(objectInfo, CancellationToken.None, NotificationOrigin.User))
            .ConfigureAwait(false);

        if (structure is null)
        {
            Fail(notification, missing);
            // 答應過回來時在 UI 執行緒上；失敗那一條也一樣。
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return;
        }

        var script = await Task.Run(() => SqlDefinitionScript.Build(structure, documentName)).ConfigureAwait(false);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var failure = SqlDefinitionScript.WriteToNewWindow(
            services, WithHeader(script, hit, unconnected), objectInfo, documentName, unconnected);

        Finish(notification, failure, OpenedNote(hit, unconnected));
    }

    /// <summary>
    /// 這一筆要開哪一種視窗：與作用中查詢視窗同一台就沿用它的連線，否則開未連線的。
    /// </summary>
    /// <returns>是不是未連線，以及發起的文件名稱（沒有查詢視窗時是空字串）。</returns>
    /// <remarks>
    /// 新視窗沿用得到的只有查詢視窗那一條連線。這一筆來自別台時沿用它，定義就落在連著另一台
    /// 的視窗裡，使用者在那裡按 F5 就是對錯的伺服器執行，而畫面看不出差別。所以那一種改開
    /// <b>未連線</b>的視窗：SSMS 在第一次執行時要求連線，什麼都不會跑在錯的那一台上。
    /// 沒有查詢視窗時同樣開未連線的——沒有連線可沿用不等於做不到。
    ///
    /// 問的是「這一筆與查詢視窗是不是同一台」，不是「有沒有指名」，也不是「範圍與查詢視窗
    /// 是不是同一台」：前者讓使用者在他自己已經連好的伺服器上也拿到一個未連線的視窗，後者在
    /// 換過查詢視窗之後恆真，而清單上的舊列來自上一台。列上事先那一句
    /// （<see cref="OpensUnconnected"/>）與這裡走同一份比對規則；這裡是按下去那一刻再問一次，
    /// 以此為準。
    /// </remarks>
    private static (bool Unconnected, string DocumentName) ChooseWindow(
        SqlSearchCatalogs catalogs, SqlSearchOrigin origin)
    {
        var view = ActiveSqlEditor.Current;
        var unconnected = view is null || !catalogs.SharesActiveEditorServer(origin);

        return (unconnected, view is null ? "" : ActiveSqlEditor.GetDocumentName(view));
    }

    /// <summary>
    /// 「移至定義」那一則通知；未連線時換一個標題，並把來源那一台放在出處上。
    /// </summary>
    /// <remarks>
    /// 標題是常數，說不出伺服器名稱，所以那一台放 <c>source</c>：通知是使用者按完之後第一個看到
    /// 的東西，只寫「移至定義」的話，他要到按 F5 跳出連線對話框才發現這個視窗沒有連線。
    /// </remarks>
    private static NotificationScope BeginGoingToDefinition(
        string subject, string documentName, SqlSearchOrigin origin, bool unconnected) =>
        NotificationCenter.Default.Begin(
            unconnected ? NotificationCatalog.GoingToDefinitionUnconnected : NotificationCatalog.GoingToDefinition,
            NotificationKind.Navigation,
            NotificationOrigin.User,
            NotificationLevel.Info,
            subject,
            documentName,
            source: unconnected ? origin.ServerName : "");

    /// <summary>
    /// 還沒開始工作就被擋下的那一種：與進行中的那一則同一個標題，直接記成失敗。
    /// </summary>
    /// <remarks>
    /// 借 <see cref="NotificationCenter.Begin"/> 開了立刻關會先冒出一列執行中再改寫；這一刻什麼都還沒做，
    /// 所以用 <see cref="NotificationCenter.Post"/>。
    /// </remarks>
    private static void Reject(string title, string subject, string message) =>
        NotificationCenter.Default.Post(title, NotificationKind.Navigation, NotificationOrigin.User,
            NotificationLevel.Info, NotificationStatus.Failed, subject, message: message);

    /// <summary>失敗的原因寫在那一則上：每一種的下一步都不同，只說「失敗」等於要使用者去翻診斷紀錄。</summary>
    private static void Fail(NotificationScope notification, string message)
    {
        notification.Report(message);
        notification.Fail();
    }

    /// <summary>開窗那一步的結果；成功時附上 <paramref name="note"/>（沒有話要說時是空字串）。</summary>
    private static void Finish(NotificationScope notification, string? failure, string note)
    {
        if (failure is not null) Fail(notification, failure);
        else if (note.Length != 0) notification.Report(note);
    }

    /// <summary>未連線時在指令碼開頭加上來源那幾行，游標跟著往後移。</summary>
    private static SqlObjectScriptText WithHeader(SqlObjectScriptText script, SearchHit hit, bool unconnected)
    {
        if (!unconnected) return script;

        var header = UnconnectedHeader(hit, Environment.NewLine);

        return header.Length == 0
            ? script
            : new SqlObjectScriptText(header + script.Text, header.Length + script.CaretOffset);
    }

    /// <summary>
    /// 把物件總管展開到這一筆指的節點並選取它。
    /// </summary>
    /// <remarks>
    /// 結果與 <see cref="ActivateAsync"/> 一樣由這裡送通知：成功、改選上一層（降級並說出停在哪一層）
    /// 與每一種失敗都在「在物件總管中選取」那一則上。<b>一律在 UI 執行緒上完成</b>。
    ///
    /// 執行緒分三段：UI 執行緒挑伺服器與目錄，背景問父物件是誰，回到 UI 執行緒逐一導航。
    /// 中間那一段由 <c>GetParentAsync</c> 自己讓出執行緒，這一層不再包一次 <c>Task.Run</c>。
    ///
    /// 伺服器由這一筆自己那一台在樹上的節點決定，與查詢視窗連著哪一台無關；
    /// <see cref="ActivateAsync">移至定義</see>則要看查詢視窗，才決定新視窗沿不沿用它的連線。
    ///
    /// 候選由 <see cref="SqlObjectExplorerUrn"/> 排好，這裡<b>依序</b>試到第一個指得到的為止，
    /// 並且說出停在哪一層。試到第二個就默默當成成功的話，使用者會以為自己正看著那個條件約束，
    /// 而選取的其實是它所屬的資料表。
    /// </remarks>
    public static async Task SelectInExplorerAsync(SearchHit hit, IServiceProvider services, SqlSearchCatalogs catalogs)
    {
        if (hit is null) throw new ArgumentNullException(nameof(hit));
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (catalogs is null) throw new ArgumentNullException(nameof(catalogs));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var title = NotificationCatalog.SelectingInObjectExplorer;

        if (!CanSelectInExplorer(hit) || OriginOf(hit) is not { } origin)
        {
            Reject(title, hit.Title, SqlSearchText.NoExplorerNode);
            return;
        }

        // 照這一筆自己的伺服器找，不照現在的範圍：換過查詢視窗或指名別台之後，範圍答的是
        // 另一台，而那一台上同名的物件會被選起來，畫面上看起來完全正常。
        if (catalogs.ResolveExplorerServer(origin) is not { } server)
        {
            Reject(title, hit.Title, SqlSearchText.NoExplorerConnection(origin));
            return;
        }

        // 接下來每一步都碰 UI（導覽服務、通知），所以 true。這裡曾經是 false，
        // 而症狀只在條件約束與觸發程序上出現——也只有那兩種真的 await 過一趟查詢，
        // 其餘種類同步完成、續程原地跑，看起來完全正常。
        var (nodes, failure) = await ResolveNodesAsync(hit, server.RootUrn, catalogs).ConfigureAwait(true);

        if (failure is not null || nodes.Count == 0)
        {
            Reject(title, hit.Title, failure ?? SqlSearchText.ExplorerNodeUnresolved);
            return;
        }

        using var notification = NotificationCenter.Default.Begin(
            title,
            NotificationKind.Navigation,
            NotificationOrigin.User,
            NotificationLevel.Info,
            hit.Title,
            // 出處是樹上那一台伺服器，不是一份文件：這條路徑沒有開任何查詢視窗。
            source: server.DisplayName);

        // 不給取消權杖：使用者按的是「帶我過去」，中途放掉等於按了沒反應。
        // 整串候選交給導航一次試完：從哪裡開始自己走只有 SqlObjectExplorerUrn 知道（AnchorUrn），
        // 同一個錨點底下的幾個候選要一趟一起找，這一層一個一個遞進去就做不到。
        var selected = await SsmsObjectExplorer
            .TrySelectFirstAsync(services, nodes, CancellationToken.None)
            .ConfigureAwait(true);

        // 比位址不比索引：資料表的第二個候選是同一個節點的另一條路（導覽服務指不到時
        // 從資料庫往下走），選到它不是降級。
        if (selected >= 0 && string.Equals(nodes[selected].Urn, nodes[0].Urn, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (selected > 0)
        {
            // 選到的是上一層：拿到了東西，但不是他要的那一個，所以是降級而不是成功。
            notification.Report(SqlSearchText.ExplorerFallback(Describe(nodes[0]), Describe(nodes[selected])));
            notification.Degrade();
            return;
        }

        // 連最寬鬆的那一個都指不到：兩種來源的下一步完全不同。
        Fail(notification, hit.ActivatePayload is SqlAgentJobSearchTarget job
            ? SqlSearchText.ExplorerJobMissing(job.JobName)
            : SqlSearchText.ExplorerObjectMissing(hit.Title));
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
    /// 問父物件用的目錄必須在這一筆那一台上（<see cref="SqlSearchCatalogs.ResolveFor"/>）：
    /// 範圍換過之後拿現在那一台的目錄去問，同號的另一個物件會交出一個看起來很正常的父物件。
    /// 那一種與「問不到」各回各的一句，第二個值就是那一句。
    ///
    /// 組位址是純字串，留在哪一條執行緒上都不影響畫面；回來時在哪一條由呼叫端的
    /// <c>await</c> 決定，這一支<b>不</b>替呼叫端切回去。恢復執行緒是階段邊界的事，
    /// 不是資料解析函式的事——寫在這裡的話，呼叫端一個 <c>ConfigureAwait(false)</c>
    /// 就能把它作廢，而看起來像是這一支失了信。真正擋住那一種錯的是
    /// <c>SsmsObjectExplorer.TrySelectFirstAsync</c> 自己切。
    /// </remarks>
    private static async Task<(IReadOnlyList<SqlExplorerNode> Nodes, string? Failure)> ResolveNodesAsync(
        SearchHit hit, string rootUrn, SqlSearchCatalogs catalogs)
    {
        // 目錄要在 UI 執行緒上問（SqlSearchCatalogs 的解析都有 assert）。呼叫端已經切過，
        // 這一步就是同步完成；自己切一次是為了讓這一支從任何地方叫都成立。
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // 這是唯一可以辨識酬載型別的地方，與啟動那一支同一條紅線。
        if (hit.ActivatePayload is SqlAgentJobSearchTarget job)
        {
            return (SqlObjectExplorerUrn.ForJob(rootUrn, job.JobName), null);
        }

        if (hit.ActivatePayload is not SqlCatalogSearchTarget target) return (Array.Empty<SqlExplorerNode>(), null);

        if (SqlObjectExplorerUrn.RequiresParent(target.Kind))
        {
            var child = ToObjectInfo(target);
            var catalog = catalogs.ResolveFor(child, target.Origin, out var elsewhere);

            if (elsewhere) return (Array.Empty<SqlExplorerNode>(), SqlSearchCatalogs.ElsewhereNotice(target.Origin));

            var parent = catalog is null
                ? null
                : await catalog
                    .GetParentAsync(child, CancellationToken.None, NotificationOrigin.User)
                    .ConfigureAwait(false);

            return parent is null
                ? (Array.Empty<SqlExplorerNode>(),
                    SqlSearchText.ExplorerParentUnknown(target.Name))
                : (SqlObjectExplorerUrn.ForChild(rootUrn, parent, target.Name), null);
        }

        return (target.ColumnName is { Length: > 0 } column
            ? SqlObjectExplorerUrn.ForColumn(
                rootUrn, target.DatabaseName, target.SchemaName, target.Name, target.Kind, column)
            : SqlObjectExplorerUrn.ForObject(
                rootUrn, target.DatabaseName, target.SchemaName, target.Name, target.Kind), null);
    }

    /// <summary>
    /// 啟動一筆作業結果：把它的步驟命令開進新查詢視窗，連線的選法與目錄物件相同。
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
    private static async Task ActivateJobAsync(
        SearchHit hit, SqlAgentJobSearchTarget job, IServiceProvider services, SqlSearchCatalogs catalogs)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // 連線一律向 SqlSearchCatalogs 要，而且只借不留：所有權在
        // SqlMetadataCatalogRegistry，留一份的症狀是換過連線之後每一次啟動都以
        // ObjectDisposedException 收場，而那不是 DbException，降級接不住。
        // 要的是<b>這一筆那一台</b>的連線：範圍換過之後拿現在那一台去 msdb 查同一個 job_id，
        // 查不到只是一句「取不到」，把另一台的命令開出來才是真的錯。
        var catalog = catalogs.ResolveOn(job.Origin, out var elsewhere);

        if (elsewhere || catalog is null)
        {
            Reject(NotificationCatalog.GoingToDefinition, job.JobName, elsewhere
                ? SqlSearchCatalogs.ElsewhereNotice(job.Origin)
                : SqlSearchText.JobConnectionMissing(job.Origin));
            return;
        }

        // 與目錄那一條同一個選擇，連判斷都同一支：這一筆來自別台伺服器時，沿用查詢視窗那條
        // 連線就是在錯的伺服器上按 F5——而一段作業步驟通常正是會改資料的那種 SQL。
        var (unconnected, documentName) = ChooseWindow(catalogs, job.Origin);
        var subject = job.StepId is { } step
            ? SqlSearchText.JobStepSubject(job.JobName, step)
            : job.JobName;

        using var notification = BeginGoingToDefinition(subject, documentName, job.Origin, unconnected);

        var script = await Task
            .Run(() => SqlAgentJobScript.TryBuild(
                catalog.ConnectionSource, job, Environment.NewLine, CancellationToken.None))
            .ConfigureAwait(false);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (script is null)
        {
            // 三個原因都要寫出來：它們的下一步完全不同（去看作業還在不在、去要 msdb 權限、
            // 去看連線），而只說「取不到」的話使用者查不出該去看哪一個。
            Fail(notification, SqlSearchText.JobCommandsMissing(job.ServerName, subject));
            return;
        }

        var failure = SqlDefinitionScript.WriteToNewWindow(
            services,
            WithHeader(new SqlObjectScriptText(script, 0), hit, unconnected),
            subject,
            SqlSearchText.JobCommandsOpened(subject),
            documentName,
            unconnected);

        Finish(notification, failure, OpenedNote(hit, unconnected));
    }
}
