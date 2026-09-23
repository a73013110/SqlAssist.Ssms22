using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Ssms22.Connections;

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
/// 非同步的 <see cref="TrySelectFirstAsync"/> 自己切，呼叫端不必先切也不必負責交還。
/// 三支都<b>只在使用者主動要求時</b>呼叫：<c>ObjectExplorerService.Tree</c> 第一次取用會以
/// <c>FTW_fForceCreate</c> 取得視窗框架並呼叫 <c>Show()</c>，使用者把物件總管關掉時，
/// 輪詢會替他把那個視窗重新叫出來。
/// </remarks>
internal static class SsmsObjectExplorer
{
    /// <summary>從錨點往下找節點的上限；逾時當成找不到，不讓那一則通知停不下來。</summary>
    private const int ChildSearchTimeoutMilliseconds = 15000;

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
    /// 依序試一串由精確到寬鬆的候選，選取第一個指得到的節點；回傳它的索引，
    /// 一個都指不到時回傳 -1。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="TryList"/> 同一個導覽服務。<see cref="SqlExplorerNode.AnchorUrn"/> 不是空的
    /// 候選多走一段 <see cref="TrySelectUnderAnchorAsync"/>：導覽服務只認得資料表、檢視與
    /// 預存程序，下不到資料表底下那幾層，也到不了「程式設計」底下的函式、序列與資料表型別，
    /// 連「系統資料庫」與「資料庫快照集」子資料夾裡的資料庫都指不到。
    /// 自己去碰 <c>ObjectExplorerControl</c> 或那棵樹一律禁止，理由見這個類別的說明。
    ///
    /// <b>同一個錨點只展開、只往下找一次。</b>錨點相同的候選（<c>DEFAULT</c> 與它的資料行，
    /// 資料表型別上的條件約束與型別本身）一趟一起找，找到幾個就選最精確的那一個。
    /// 一個一個試的症狀是第一個對不上時同一個錨點再展開、再翻一遍資料夾，而每一趟都各有一次
    /// 逾時可以等。
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
    public static async Task<int> TrySelectFirstAsync(
        IServiceProvider services, IReadOnlyList<SqlExplorerNode> candidates, CancellationToken cancellationToken)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (ResolveNavigation(services) is not { } navigation) return -1;

        var searchedAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reachedAnchors = new List<string>();

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];

            // 導覽服務自己指得到的那幾種（資料表、檢視、預存程序、作業）。
            if (candidate.AnchorUrn.Length == 0)
            {
                if (await navigation.NavigateToUrnAsync(candidate.Urn, cancellationToken).ConfigureAwait(true))
                {
                    return index;
                }

                continue;
            }

            if (IsCovered(candidate, reachedAnchors)) continue;

            // 這個錨點底下的候選已經一起找過了：找到的話早就回傳，這裡只剩沒找到。
            if (!searchedAnchors.Add(candidate.AnchorUrn)) continue;

            // 先讓導覽服務把樹展開到錨點（資料表、資料庫或伺服器），展開的正好是接下來要找的
            // 那一層。到不了（系統資料庫、FileTable）就輪到同一個目標更遠的那個錨點。
            if (!await navigation.NavigateToUrnAsync(candidate.AnchorUrn, cancellationToken).ConfigureAwait(true))
            {
                continue;
            }

            reachedAnchors.Add(candidate.AnchorUrn);

            var siblings = new List<int>();

            for (var next = index; next < candidates.Count; next++)
            {
                if (string.Equals(candidates[next].AnchorUrn, candidate.AnchorUrn, StringComparison.OrdinalIgnoreCase) &&
                    !IsCovered(candidates[next], reachedAnchors))
                {
                    siblings.Add(next);
                }
            }

            var nodes = siblings.ConvertAll(sibling => candidates[sibling]);

            if (await TrySelectUnderAnchorAsync(services, candidate.AnchorUrn, nodes, cancellationToken)
                    .ConfigureAwait(true) is { } rank)
            {
                return siblings[rank];
            }
        }

        return -1;
    }

    /// <summary>
    /// 這個候選在一個已經到得了、而且比它的錨點更近的錨點底下：從遠的地方再走一次只會
    /// 經過同一段，一樣找不到。
    /// </summary>
    /// <remarks>
    /// 同一個目標由近到遠有好幾條路（資料表、資料庫、伺服器根）。近的那一條<b>到得了</b>而
    /// 找不到，代表它不在樹上（卸除了、被篩選器擋掉），遠的那一條只是把同一段重翻一遍，
    /// 還多翻了沿路那些資料夾，各有一次逾時可以等。近的<b>到不了</b>才輪到遠的——系統資料庫
    /// 與快照集導覽服務指不到，只能從伺服器根下去。錨點本身就是目標時（條件約束的資料表
    /// 已經被導覽到過）也算，那個目標後面還有導覽服務直接指的那一條。
    /// </remarks>
    private static bool IsCovered(SqlExplorerNode candidate, IReadOnlyList<string> reachedAnchors)
    {
        foreach (var reached in reachedAnchors)
        {
            if (IsBelow(reached, candidate.AnchorUrn) &&
                (string.Equals(candidate.Urn, reached, StringComparison.OrdinalIgnoreCase) ||
                    IsBelow(candidate.Urn, reached)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="urn"/> 是 <paramref name="ancestor"/> 底下的位址（不含相等）。</summary>
    /// <remarks>大小寫不敏感，理由與 <see cref="ChildSearch"/> 相同。</remarks>
    private static bool IsBelow(string urn, string ancestor) =>
        urn.Length > ancestor.Length &&
        urn[ancestor.Length] == '/' &&
        urn.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 從錨點的節點往下找出 <paramref name="nodes"/> 裡最前面的那一個並選取它；
    /// 回傳它在 <paramref name="nodes"/> 裡的位置，一個都找不到時回傳 null。
    /// </summary>
    /// <remarks>
    /// <b>為什麼不能只靠導覽服務。</b>SSMS 22 的 <c>NavigateToUrnAsync</c> 從資料庫往下只認得
    /// 三種物件：<c>SchemaObjectFolderPaths</c> 寫死 <c>UserTables</c>、<c>Views</c>、
    /// <c>UserProgrammability/StoredProcedures</c>。其餘一段它只比對父節點的<b>直接</b>子節點，
    /// 比不中就查那張表，查不到就回 false。所以函式、同義字、序列與資料表型別從樹根一個都
    /// 指不到（它們隔著「程式設計」「函式」幾層資料夾），而資料表底下的條件約束、觸發程序、
    /// 索引與資料行一律停在資料表上（資料表的直接子節點全是資料夾）。FileTable 與圖形資料表
    /// 畫在「資料表」的子資料夾裡，系統資料庫與快照集畫在「資料庫」的子資料夾裡，同一個症狀。
    /// 同一個服務的 <c>ExpandNodeAsync</c>／<c>GetNodeChildrenAsync</c> 也替代不了：兩支都先用
    /// URN 找節點，而資料夾的 URN 就是父節點的，問到的永遠是父節點那一層。
    ///
    /// <b>為什麼不用 <c>FindNode(節點位址)</c> 代勞。</b>那一支確實下得到任何一層，代價卻是
    /// 從樹根開始的<b>全樹搜尋</b>：每一層都會把沿路資料夾的子節點建出來，而「建出來」就是
    /// 向伺服器查詢。節點已經建好時它是字典查詢（<c>FindBuiltNodes</c>）所以很快；<b>沒建好
    /// 的那一次</b>會一路翻進資料庫底下每一個資料夾、再翻進伺服器底下的安全性、複寫、管理與
    /// Agent——大的伺服器上那是幾分鐘起跳，而畫面上只有一則停不下來的「在物件總管中選取」。
    /// 走這條路時<b>禁止</b>拿它去找還沒建出來的節點。
    ///
    /// 所以這一支只做外科手術式的那一段：錨點剛剛才被導覽服務選到，從
    /// <c>GetSelectedNodes</c> 拿它的 <c>INavigableItem</c>，往下只建通往候選的那幾層。
    /// 連 <c>FindNode(錨點)</c> 都不用：它列舉的是 WinForms 控制項上的
    /// <c>Hierarchies</c> 字典，不該離開 UI 執行緒，找不到時還會退回全樹搜尋。
    ///
    /// <b>往下走那一段在背景執行緒上，這是 SSMS 自己的用法。</b>樹展開節點時由
    /// <c>ExplorerHierarchyNode.BuildChildren</c> 在工作執行緒上呼叫
    /// <c>INavigableItem.RequestChildren</c>（<c>GetChildren</c> 是它的同步包裝），
    /// <c>NavigableItem</c> 以鎖與累加器序列化：樹正在展開同一個節點時，我們這一趟併進去等
    /// 同一份結果，不會再查一次。放在 UI 執行緒上才是錯的——沒建過的資料夾會讓它停在伺服器
    /// 查詢上。
    ///
    /// 那一段有<see cref="ChildSearchTimeoutMilliseconds">逾時</see>：伺服器忙起來時
    /// <c>GetChildren</c> 會停在物件總管自己那一輪建構上，沒有上限。逾時靠
    /// <c>WithCancellation</c> 放掉<b>等待</b>，不靠 <c>Task.Run</c> 的權杖：後者只擋還沒開始的
    /// 工作，<c>GetChildren</c> 一旦開跑，<c>await</c> 就一路等到它回來，而 <c>_selecting</c>
    /// 那一關讓按鈕在那段時間裡按不動。逾時前已經找到的候選照樣算數：資料行先找到、條件約束
    /// 還在翻下一個資料夾時逾時，選資料行比退回整張表更接近。
    ///
    /// 放掉的那一趟還在跑，它之後才失敗的話沒有人接，所以交給
    /// <see cref="SqlAssistPlatformGuard.BeginProbe(string, Task)"/>：它只是把節點建出來，
    /// 失敗代表下一次按要自己付那一趟，跟預先載入同一個層級。
    /// </remarks>
    private static async Task<int?> TrySelectUnderAnchorAsync(
        IServiceProvider services, string anchorUrn, IReadOnlyList<SqlExplorerNode> nodes,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (ResolveExplorer(services) is not { } explorer) return null;

        if (SelectedItem(explorer, anchorUrn) is not { } anchor)
        {
            SqlAssistDiagnostics.WriteAlways($"導覽到 {anchorUrn} 之後選取的不是它，改用下一個候選");
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ChildSearchTimeoutMilliseconds);

        // 深度照最深的那一個候選：同一趟裡資料表型別本身在第四層，它的條件約束在第六層。
        var depth = nodes.Max(node => node.Depth);
        var search = new ChildSearch(nodes, deadline.Token);
        var walk = Task.Run(() => search.Under(anchor, depth), deadline.Token);

        try
        {
            await walk.WithCancellation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SqlAssistPlatformGuard.BeginProbe("在物件總管中往下找子節點（已放掉等待）", walk);

            if (cancellationToken.IsCancellationRequested) throw;

            SqlAssistDiagnostics.WriteAlways($"在 {anchorUrn} 底下找子節點逾時，改用已找到的最接近候選");
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (search.Best() is not { } best) return null;

        explorer.SynchronizeTree(best.Node);

        return best.Rank;
    }

    /// <summary>目前選取的那一個節點就是 <paramref name="urn"/> 時交出它，否則回傳 null。</summary>
    /// <remarks>
    /// 比對是必要的：導覽完成到這裡之間 UI 執行緒可能處理過使用者的點選，拿錯的節點往下找
    /// 會在另一張表底下選到同名的東西。大小寫不敏感，理由與 <see cref="ChildSearch"/> 相同。
    /// </remarks>
    private static INavigableItem? SelectedItem(IObjectExplorerService explorer, string urn)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        explorer.GetSelectedNodes(out _, out var selected);

        return selected is { Length: 1 } &&
            string.Equals(selected[0]?.Context, urn, StringComparison.OrdinalIgnoreCase)
            ? selected[0].GetService(typeof(INavigableItem)) as INavigableItem
            : null;
    }

    /// <summary>
    /// 從一個節點往下找候選位址；記住找到的最精確那一個，找到第一名就停。
    /// </summary>
    /// <remarks>
    /// 只往兩種子節點裡走：<b>資料夾</b>——樹上的資料夾沒有自己的位址，它的 <c>Context</c>
    /// 就是父節點的，而別的東西一律有自己的——以及位址是某個候選<b>前綴</b>的物件
    /// （資料表型別之於它的條件約束）。靠這兩條遞迴，搜尋範圍天生就關在通往候選的路上，
    /// 不會翻進別的資料表或別的函式底下。資料夾不拿來比候選：它的位址與父節點相同，
    /// 比中的話會把資料夾當成父節點選起來。
    ///
    /// 照候選的 <see cref="SqlExplorerNode.Folders"/> 排過再翻：每翻開一個沒建過的資料夾
    /// 就是一趟伺服器查詢，照樹的順序找函式要先付資料表、檢視與外部資源好幾趟。
    /// 只排序不過濾，名稱對不上的資料夾排在後面照樹的順序翻，最壞就是原本的成本。
    ///
    /// 深度<b>要有上限</b>，由候選的 <see cref="SqlExplorerNode.Depth"/> 給：這是一段會向伺服器
    /// 查詢的遞迴，而樹上的資料夾巢狀多深不由我們決定。
    ///
    /// 比對大小寫不敏感，與物件總管自己的 <c>NotifyHandler.FindItem</c> 同一條規則：
    /// 位址是我們照目錄組的，而樹上那一份來自 SMO，兩邊的大小寫沒有人保證一致。
    ///
    /// 背景執行緒寫、UI 執行緒在逾時後讀，所以結果放在鎖後面；逾時之後背景那一趟還在跑，
    /// 它每翻一個子節點就看一次權杖，不會在放掉之後繼續往下建。
    /// </remarks>
    private sealed class ChildSearch
    {
        private readonly IReadOnlyList<SqlExplorerNode> _nodes;
        private readonly CancellationToken _cancellationToken;
        private readonly object _gate = new();
        private INodeInformation? _node;
        private int _rank = int.MaxValue;

        public ChildSearch(IReadOnlyList<SqlExplorerNode> nodes, CancellationToken cancellationToken)
        {
            _nodes = nodes;
            _cancellationToken = cancellationToken;
        }

        public (INodeInformation Node, int Rank)? Best()
        {
            lock (_gate) return _node is null ? null : (_node, _rank);
        }

        /// <returns>找到第一名（不必再找）時為 true。</returns>
        public bool Under(INavigableItem item, int depth)
        {
            var itemContext = item.Context?.Context;
            var next = new List<(INavigableItem Item, (int Candidate, int Step) Preference)>();

            foreach (var child in item.GetChildren(ItemScope.Any) ?? Array.Empty<INavigableItem>())
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (child?.Context?.Context is not { Length: > 0 } context) continue;

                if (string.Equals(context, itemContext, StringComparison.OrdinalIgnoreCase))
                {
                    if (depth > 1) next.Add((child, FolderPreference(child.Context.InvariantName)));

                    continue;
                }

                if (Rank(context) is { } rank && Offer(child.Context, rank)) return true;

                if (depth > 1 && AncestorPreference(context) is { } preference) next.Add((child, preference));
            }

            // OrderBy 是穩定排序：沒有提示的資料夾維持樹的順序。
            foreach (var (child, _) in next.OrderBy(entry => entry.Preference))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (Under(child, depth - 1)) return true;
            }

            return false;
        }

        /// <summary>越精確的候選路上經過的資料夾越先翻，同一個候選照沿路順序；認不得的排最後。</summary>
        private (int, int) FolderPreference(string? name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                for (var index = 0; index < _nodes.Count; index++)
                {
                    var folders = _nodes[index].Folders;

                    for (var step = 0; step < folders.Count; step++)
                    {
                        if (string.Equals(folders[step], name, StringComparison.OrdinalIgnoreCase)) return (index, step);
                    }
                }
            }

            return (int.MaxValue, 0);
        }

        /// <summary>位址是某個候選的前綴時，那個候選越精確就越先翻；不是任何候選的前綴時回傳 null。</summary>
        /// <remarks>排在同一個候選的資料夾前面：它確定在路上，資料夾只是名稱對得上。</remarks>
        private (int, int)? AncestorPreference(string context)
        {
            for (var index = 0; index < _nodes.Count; index++)
            {
                if (IsBelow(_nodes[index].Urn, context)) return (index, -1);
            }

            return null;
        }

        private int? Rank(string context)
        {
            for (var index = 0; index < _nodes.Count; index++)
            {
                if (string.Equals(context, _nodes[index].Urn, StringComparison.OrdinalIgnoreCase)) return index;
            }

            return null;
        }

        private bool Offer(INodeInformation node, int rank)
        {
            lock (_gate)
            {
                if (rank < _rank)
                {
                    _node = node;
                    _rank = rank;
                }

                return _rank == 0;
            }
        }
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
