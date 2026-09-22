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
    /// 與 <see cref="TryList"/> 同一個導覽服務。自己去碰 <c>ObjectExplorerControl</c> 或那棵樹
    /// 一律禁止，理由見這個類別的說明。
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
        IServiceProvider services, string urn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(urn)) throw new ArgumentException("節點 URN 不可為空。", nameof(urn));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (ResolveNavigation(services) is not { } navigation) return false;

        return await navigation.NavigateToUrnAsync(urn, cancellationToken).ConfigureAwait(true);
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
