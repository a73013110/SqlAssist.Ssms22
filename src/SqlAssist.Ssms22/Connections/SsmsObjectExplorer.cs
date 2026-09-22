using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 物件總管目前連著的一台 SQL Server。
/// </summary>
/// <remarks>
/// 只留識別用的字串，<b>不留連線也不留任何祕密</b>：要開連線時再回頭問物件總管。
/// 這個值會進 UI 的下拉選項與已選條件 chip，留著連線等於把密碼綁在一個畫面物件上。
/// </remarks>
internal sealed class SsmsObjectExplorerServer
{
    internal SsmsObjectExplorerServer(string displayName, string serverName, string rootUrn)
    {
        DisplayName = displayName;
        ServerName = serverName;
        RootUrn = rootUrn;
    }

    /// <summary>物件總管樹上顯示的那一個名稱；使用者是照這個名字認伺服器的。</summary>
    public string DisplayName { get; }

    /// <summary>連線字串裡那個伺服器名稱；用來與查詢視窗那一條連線比對。</summary>
    public string ServerName { get; }

    /// <summary>根節點的 URN；回頭向物件總管要連線時就是靠它。</summary>
    public string RootUrn { get; }
}

/// <summary>
/// 向 SSMS 的物件總管要東西的唯一入口：目前連著哪幾台伺服器、其中一台的連線，
/// 以及把樹展開到某一個節點上。
/// </summary>
/// <remarks>
/// 走的是 SSMS 22 的 <c>IObjectExplorerNavigationService</c>（服務型別
/// <c>SObjectExplorerNavigationService</c>），也就是 SSMS 自己的 Copilot
/// <c>oe_get_servers</c> 走的那一條，型別在已經參考的 <c>SqlWorkbench.Interfaces</c> 裡。
/// <b>禁止</b>改用反射去碰 <c>ObjectExplorerControl</c> 或 <c>ConnectionCache</c>——
/// 兩個都是 internal，而公開路徑拿得到同樣的東西。
///
/// 連線本身不在導覽服務上：<c>GetConnectedServers()</c> 只交得出名稱、驗證方式與登入名，
/// 沒有密碼也沒有 token，SQL 驗證與 Entra 都連不上去。要連線得再走一步
/// <c>IObjectExplorerService.FindNode(RootUrn)</c> 拿到節點自己的
/// <see cref="SqlOlapConnectionInfoBase"/>，那一份是 SSMS 建樹時就帶著認證的。
///
/// 同步的那兩支<b>只能在 UI 執行緒上</b>呼叫（同步方法切不了執行緒，所以維持 assert）；
/// 非同步的 <see cref="TryNavigateAsync"/> 自己切，呼叫端不必先切也不必負責交還。
/// 三支都<b>只在使用者主動要求時</b>呼叫：<c>ObjectExplorerService.Tree</c> 第一次取用會以
/// <c>FTW_fForceCreate</c> 取得視窗框架並呼叫 <c>Show()</c>，使用者把物件總管關掉時，
/// 輪詢會替他把那個視窗重新叫出來。
/// </remarks>
internal static class SsmsObjectExplorer
{
    /// <summary>在父物件底下找子節點的上限；逾時當成找不到，不讓那一則通知停不下來。</summary>
    private const int ChildSearchTimeoutMilliseconds = 15000;

    /// <summary>往下找幾層：物件底下一層資料夾、資料夾底下就是節點。</summary>
    private const int ChildSearchDepth = 2;

    /// <summary>
    /// 物件總管上已連線的 SQL Server；取不到服務時回傳 null。
    /// </summary>
    /// <remarks>
    /// null 與空清單是兩件事，呼叫端要分開說：前者是「問不到物件總管」，
    /// 後者是「物件總管上沒有 SQL Server」。合成一種的症狀是使用者看到一份
    /// 看起來完整的清單，卻不知道少了幾台。
    ///
    /// 只列得出 <see cref="SqlConnectionInfo"/> 的節點。物件總管的樹也可能是
    /// Analysis Services、Integration Services 或 Azure Data Factory，而
    /// <c>OEServerInfo</c> 沒有任何欄位分得出來——照單全收的話，下拉裡會多出幾台
    /// 選了就整輪失敗的「伺服器」。
    ///
    /// 這一趟沒有任何資料庫 I/O：導覽服務只走已經建好的樹，<c>FindNode</c> 也只是
    /// 在根節點之間比對 URN。
    /// </remarks>
    public static IReadOnlyList<SsmsObjectExplorerServer>? TryList(IServiceProvider services)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ResolveNavigation(services) is not { } navigation) return null;
        if (ResolveExplorer(services) is not { } explorer) return null;

        var servers = new List<SsmsObjectExplorerServer>();

        foreach (var server in navigation.GetConnectedServers())
        {
            if (server is not { IsConnected: true } || string.IsNullOrEmpty(server.RootUrn)) continue;
            if (TryResolveConnectionInfo(explorer, server.RootUrn) is null) continue;

            // DisplayName 是物件總管樹上那一行，ServerName 是連線字串裡的名稱；
            // 具名執行個體與有別名的連線上兩者不同，而使用者認的是前者。
            // 兩個都空就退回 URN：沒有名字的那一行比少一行更難發現。
            var serverName = server.ServerName ?? "";
            var displayName = server.DisplayName is { Length: > 0 } name ? name : serverName;

            servers.Add(new SsmsObjectExplorerServer(
                displayName.Length == 0 ? server.RootUrn : displayName,
                serverName,
                server.RootUrn));
        }

        return servers;
    }

    /// <summary>
    /// 以物件總管那台伺服器的認證，另外開一條連線的來源；拿不到時回傳 null。
    /// </summary>
    /// <remarks>
    /// <c>CreateConnectionObject()</c> 交出來的是一條<b>全新、尚未開啟</b>的連線，
    /// 不是物件總管正在用的那一條——兩邊共用一條就是共用一個 SPID，物件總管展開節點時
    /// 我們這一輪就卡住。Entra 的 access token 也是在這一刻才向 <c>IRenewableToken</c> 取。
    ///
    /// 交出去之後所有權就歸 <c>SqlMetadataCatalogRegistry</c>，與查詢視窗那一條完全同一套：
    /// 同一個快取鍵已經有目錄時，這一份會被當成重複的當場釋放，所以呼叫端<b>禁止</b>留一份。
    /// 樣板連線因此是一個工作階段建一次，Entra 的 token 跟著它定格——那是
    /// <see cref="SsmsConnectionSource"/> 與註冊表既有的性質，查詢視窗那一條也一樣，
    /// 要修就修在那裡，不在這一支各補一套。
    /// </remarks>
    public static SsmsConnectionSource? TryCreateConnectionSource(
        IServiceProvider services, SsmsObjectExplorerServer server)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (server is null) throw new ArgumentNullException(nameof(server));

        if (ResolveExplorer(services) is not { } explorer ||
            TryResolveConnectionInfo(explorer, server.RootUrn) is not { } connectionInfo)
        {
            SqlAssistDiagnostics.WriteAlways($"物件總管上找不到 {server.DisplayName} 的連線，略過這一台");
            return null;
        }

        // 這裡不吞例外：建不出連線與「這一台不在了」要分得開，呼叫端才說得出哪一句。
        // 失敗時 SsmsConnectionSource.TryCreate 自己會記一行並回 null。
        return SsmsConnectionSource.TryCreate(connectionInfo.CreateConnectionObject());
    }

    /// <summary>
    /// 把物件總管展開到這個 URN 指到的節點並選取它；樹上沒有那個節點時回傳 false。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="TryList"/> 同一個導覽服務。<paramref name="ownerUrn"/> 不是空的時候多走
    /// 一段 <see cref="TrySelectUnderOwnerAsync"/>：導覽服務下不到資料表底下那幾層，
    /// 而那正是資料行、條件約束與觸發程序所在的地方。自己去碰 <c>ObjectExplorerControl</c>
    /// 或那棵樹一律禁止，理由見這個類別的說明。
    ///
    /// <b>不</b>吞例外：使用者是自己按出這一步的，安靜地什麼都不做等於故障，呼叫端要把
    /// 每一種失敗寫到看得見的地方。取不到服務是唯一的例外——物件總管套件按需載入，
    /// 那一步照舊走 Probe 並回 false。
    ///
    /// 這一趟會把物件總管的視窗叫出來、搶走焦點，展開節點還要向伺服器問資料，大的資料庫上
    /// 是好幾秒。兩件事都只准在使用者自己要求時發生，所以這一支<b>禁止</b>掛在選取變更或
    /// 任何輪詢上。
    ///
    /// <b>UI 親和性由這一支自保</b>，不靠呼叫端交還。續程落在哪一條執行緒是呼叫端的
    /// <c>await</c> 選項決定的——被呼叫端就算在回傳前切回 UI 執行緒，呼叫端一個
    /// <c>ConfigureAwait(false)</c> 就會把續程丟回執行緒集區（UI 執行緒上有
    /// Dispatcher 的同步內容，續程不准內聯），而那只有在中間真的 await 過的路徑上才發作：
    /// 症狀是同一顆按鈕在資料表上好好的，在條件約束上丟
    /// <c>must be called on the UI thread</c>。已經在 UI 執行緒時這一步是同步完成的
    /// （<c>SwitchToMainThreadAsync</c> 當場回 <c>IsCompleted</c>），不排訊息也不讓出執行緒，
    /// 所以擺在這裡不花錢。
    ///
    /// 同一個理由套不到 <see cref="TryList"/> 與 <see cref="TryCreateConnectionSource"/>：
    /// 同步方法切不了執行緒，那兩支維持 assert。
    ///
    /// 可以從任何執行緒起呼叫，但<b>禁止</b>用 <c>JoinableTaskFactory.Run</c> 之類的方式
    /// 同步等它：UI 執行緒被擋住時，切回去的那一步永遠等不到，而畫面上看起來就是整個
    /// SSMS 凍住。呼叫端一律讓它跑在非同步路徑上。
    /// </remarks>
    public static async Task<bool> TryNavigateAsync(
        IServiceProvider services, string urn, string ownerUrn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(urn)) throw new ArgumentException("節點 URN 不可為空。", nameof(urn));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (ResolveNavigation(services) is not { } navigation) return false;

        // 自己就有位址的那幾種（物件本身、作業）：導覽服務從樹根指得到。
        if (string.IsNullOrEmpty(ownerUrn) || string.Equals(ownerUrn, urn, StringComparison.Ordinal))
        {
            return await navigation.NavigateToUrnAsync(urn, cancellationToken).ConfigureAwait(true);
        }

        // 畫在父物件底下的那幾種：先讓導覽服務把樹展開到父物件，這一段它做得到，
        // 而且展開的正好是接下來要找的那一層。
        if (!await navigation.NavigateToUrnAsync(ownerUrn, cancellationToken).ConfigureAwait(true)) return false;

        return await TrySelectUnderOwnerAsync(services, ownerUrn, urn, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 在父物件的節點底下找出這個節點並選取它；找不到或逾時回傳 false。
    /// </summary>
    /// <remarks>
    /// <b>為什麼不能只靠導覽服務。</b>SSMS 22 的 <c>NavigateToUrnAsync</c> 只認得四種中間
    /// 資料夾（<c>Databases</c>、<c>UserTables</c>、<c>Views</c>、
    /// <c>UserProgrammability/StoredProcedures</c>）。位址再深一段時它展開父節點、比對父節點的
    /// <b>直接</b>子節點，而資料表的直接子節點全是資料夾，一個都比不中，接著查那張寫死的
    /// 對應表，查不到就回 false。症狀是條件約束、觸發程序、索引與資料行一律停在資料表上。
    ///
    /// <b>為什麼不用 <c>FindNode(節點位址)</c> 代勞。</b>那一支確實下得到任何一層，代價卻是
    /// 從樹根開始的<b>全樹搜尋</b>：每一層都會把沿路資料夾的子節點建出來，而「建出來」就是
    /// 向伺服器查詢。節點已經建好時它是字典查詢（<c>FindBuiltNodes</c>）所以很快；<b>沒建好
    /// 的那一次</b>會一路翻進資料庫底下每一個資料夾、再翻進伺服器底下的安全性、複寫、管理與
    /// Agent——大的伺服器上那是幾分鐘起跳，而畫面上只有一則停不下來的「在物件總管中選取」。
    /// 走這條路時<b>禁止</b>拿它去找還沒建出來的節點。
    ///
    /// 所以這一支只做外科手術式的那一段：父物件的節點剛剛才被選到，所以
    /// <c>FindNode(父物件)</c> 是字典查詢；從它的 <c>INavigableItem</c> 往下走，只建它自己
    /// 底下那幾個資料夾。資料夾靠「<c>Context</c> 與父節點相同」認出來——樹上只有資料夾
    /// 沒有自己的位址，而這一條同時把搜尋<b>關在這個物件底下</b>，不會外溢到別的分支。
    ///
    /// 建資料夾會向伺服器查詢，所以那一段在背景執行緒上，並且有
    /// <see cref="ChildSearchTimeoutMilliseconds">逾時</see>：使用者按的是一顆按鈕，
    /// 而伺服器忙起來時 <c>GetChildren</c> 會停在物件總管自己那一輪建構上，沒有上限。
    /// 逾時就當成找不到，呼叫端接著試下一個候選（父物件），頁尾照實說「已改為選取…」。
    /// 放掉的那個工作不必收——它只是把節點建出來，下一次按就是現成的。
    /// </remarks>
    private static async Task<bool> TrySelectUnderOwnerAsync(
        IServiceProvider services, string ownerUrn, string urn, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (ResolveExplorer(services) is not { } explorer) return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ChildSearchTimeoutMilliseconds);

        INodeInformation? node;

        try
        {
            // 連 FindNode(父物件) 都放進來：它應該是字典查詢，而「應該」不值得拿 UI 執行緒賭。
            // 那一支找不到時會退回全樹搜尋，擺在這裡的話最壞情形也只是逾時。
            node = await Task
                .Run(
                    () => explorer.FindNode(ownerUrn)?.GetService(typeof(INavigableItem)) is INavigableItem item
                        ? FindUnder(item, urn, ChildSearchDepth)
                        : null,
                    deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            node = null;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (node is null) return false;

        explorer.SynchronizeTree(node);

        return true;
    }

    /// <summary>
    /// 從一個節點往下找出位址是 <paramref name="urn"/> 的那一個；沒有就回 null。
    /// </summary>
    /// <remarks>
    /// 只認兩種子節點：位址就是要找的那一個，以及<b>資料夾</b>——樹上的資料夾沒有自己的位址，
    /// 它的 <c>Context</c> 就是父節點的，而別的東西一律有自己的。靠這一條遞迴，搜尋範圍
    /// 天生就關在這個物件底下。
    ///
    /// 深度<b>要有上限</b>：這是一段會向伺服器查詢的遞迴，而樹上的資料夾巢狀多深不由我們決定。
    /// 兩層就夠——物件底下一層資料夾、資料夾底下就是節點；<c>DEFAULT</c> 的位址雖然多一段
    /// 資料行，畫它的仍然是資料表的那個資料夾。
    ///
    /// 比對大小寫不敏感，與物件總管自己的 <c>NotifyHandler.FindItem</c> 同一條規則：
    /// 位址是我們照目錄組的，而樹上那一份來自 SMO，兩邊的大小寫沒有人保證一致。
    /// </remarks>
    private static INodeInformation? FindUnder(INavigableItem item, string urn, int depth)
    {
        var ownerContext = item.Context?.Context;

        foreach (var child in item.GetChildren(ItemScope.Any) ?? Array.Empty<INavigableItem>())
        {
            if (child?.Context?.Context is not { Length: > 0 } context) continue;

            if (string.Equals(context, urn, StringComparison.OrdinalIgnoreCase)) return child.Context;

            if (depth <= 1 || !string.Equals(context, ownerContext, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FindUnder(child, urn, depth - 1) is { } found) return found;
        }

        return null;
    }

    /// <remarks>
    /// 只認 <see cref="SqlConnectionInfo"/>：它才帶得出
    /// <c>Microsoft.Data.SqlClient</c> 的連線，其他 <c>ServerType</c> 的子類交出來的是
    /// 我們的中繼資料查詢完全用不上的東西。
    /// </remarks>
    private static SqlConnectionInfo? TryResolveConnectionInfo(IObjectExplorerService explorer, string rootUrn)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // 節點找不到就是那一台剛被中斷；這是常態而不是故障，走 Probe 不灌紀錄檔。
        return SqlAssistPlatformGuard.Probe<SqlConnectionInfo?>(
            "取得物件總管節點的連線",
            () => explorer.FindNode(rootUrn) is INodeContext context ? context.Connection as SqlConnectionInfo : null,
            fallback: null);
    }

    /// <remarks>
    /// 先問傳進來的服務提供者，再退回全域服務——與 SSMS 自己的 <c>ServiceCache</c>
    /// 同一個順序，第一支在殼層還沒把 SqlAssist 完全 site 好時會落空。
    /// 物件總管套件是按需載入的，取不到不代表壞掉，所以走 Probe 而不是 Run。
    /// </remarks>
    private static IObjectExplorerNavigationService? ResolveNavigation(IServiceProvider services) =>
        Resolve<IObjectExplorerNavigationService>(services, typeof(SObjectExplorerNavigationService));

    private static IObjectExplorerService? ResolveExplorer(IServiceProvider services) =>
        Resolve<IObjectExplorerService>(services, typeof(IObjectExplorerService));

    private static T? Resolve<T>(IServiceProvider services, Type serviceType) where T : class
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return SqlAssistPlatformGuard.Probe<T?>(
            "取得 SSMS 物件總管服務",
            () => services.GetService(serviceType) as T ?? Package.GetGlobalService(serviceType) as T,
            fallback: null);
    }
}
