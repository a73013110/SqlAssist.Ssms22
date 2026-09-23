using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// SQL Search 這一輪要用哪一份中繼資料目錄——清單、預覽與移至定義<b>唯一</b>的出處。
/// </summary>
/// <remarks>
/// 三條路徑各自去問「目錄在哪裡」的話，換了伺服器之後只要有一條忘了換，
/// 它就會拿另一台伺服器上同號的物件回答，而畫面上看不出跳錯了——
/// <c>object_id</c> 跨伺服器毫無關係，這是整個功能最貴的一種錯。
///
/// 伺服器是使用者<b>明確選的</b>一台，沒有「跟著查詢視窗」這種會自己變的狀態：切分頁、在查詢視窗
/// 換連線都不動它。來源有兩種，對上面三條路徑是同一種東西：物件總管上已連線的一台
/// （<see cref="Select(SsmsObjectExplorerServer)"/>），或從查詢視窗抓下來的那條連線
/// （<see cref="ReadActiveEditorAsync"/> 再 <see cref="Select(SqlSearchConnection)"/>）。後者讓物件總管上
/// 沒有的那一台也搜得到；抓下來的是註冊表裡的編輯器目錄，它不會被淘汰，而連線來源是複製出來的
/// 樣板，查詢視窗關掉之後照樣可用。兩種都連同伺服器一起交出（<see cref="SqlSearchConnection"/>），
/// 不另外問一次。
///
/// <b>禁止</b>持有 <c>ISqlConnectionSource</c>：所有權在
/// <see cref="SqlMetadataCatalogRegistry"/>，同一個快取鍵重複建立時多出來的那一份會當場
/// 釋放，留著的症狀是之後每一輪都以 <see cref="ObjectDisposedException"/> 收場，
/// 而那不是 <see cref="System.Data.Common.DbException"/>，索引那一層的降級接不住。
/// 這裡留的是<b>目錄</b>，與 <see cref="SqlSearchProviders"/> 同一個規矩。
///
/// 全部方法都只能在 UI 執行緒上呼叫：作用中編輯器與物件總管的服務都有 UI 相依性。
///
/// <b>一筆結果點下去時，伺服器照那一筆的 <see cref="SqlSearchOrigin"/>，不照現在的範圍。</b>
/// 清單比範圍活得久：換了伺服器之後，舊的那幾列仍然指向上一台，而現在的範圍答的是換過之後
/// 那一台。所以導航、沿用連線與目錄查詢三條路都把那一筆的伺服器帶進來問，
/// 範圍不在那一台時照實拒絕，不拿現在這一台同名、同號的東西代答。
/// 純判斷那一半（比對規則、在樹上挑哪一台）在 <c>SqlSearchCatalogs.Servers.cs</c>，零 VS 相依。
/// </remarks>
internal sealed partial class SqlSearchCatalogs
{
    private readonly IServiceProvider _services;

    /// <summary>選的是物件總管上的哪一台；選的是查詢視窗那條連線或還沒選時為 null。</summary>
    private SsmsObjectExplorerServer? _explorer;

    /// <summary>
    /// 選定那一台的目錄與伺服器；物件總管那一台在第一次解析時才建。
    /// </summary>
    /// <remarks>
    /// 留著是因為每一輪搜尋、每一次選取列都會問一次，而重建要向物件總管走一趟
    /// <c>FindNode</c> 再配一條連線——那都在 UI 執行緒上。目錄本身是註冊表共用的，
    /// 留一份參考沒有所有權問題。
    /// </remarks>
    private SqlSearchConnection? _selected;

    internal SqlSearchCatalogs(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>選的是物件總管上的哪一台；導航在樹上找那一台時優先用它。</summary>
    public SsmsObjectExplorerServer? Server => _explorer;

    /// <summary>選定那一台叫什麼；還沒選時為 null。按鈕摘要與連不上時那一句都用它。</summary>
    public string? ServerName => _explorer?.DisplayName ?? _selected?.Origin.ServerName;

    /// <summary>
    /// 選定的就是這一台；比的是伺服器名稱，不管它是從物件總管還是查詢視窗選來的。
    /// </summary>
    /// <remarks>
    /// 同一台在下拉裡只列一行，所以勾選照名稱畫：從查詢視窗套用的那一台就是物件總管上
    /// 那一行，而使用者再點一次它不該換掉查詢視窗那條連線（登入可能不同）。
    /// </remarks>
    public bool IsSelected(string? serverName) =>
        IsSameServer(serverName, _explorer?.ServerName ?? _selected?.Origin.ServerName);

    /// <summary>選定的就是物件總管上這一行。</summary>
    /// <remarks>
    /// 選的是物件總管上的一台時比節點：同一台在樹上可能有兩條連線（不同登入），照名稱比的話兩行都打勾。
    /// 選的是查詢視窗那條連線時照名稱，理由見 <see cref="IsSelected(string?)"/>。
    /// </remarks>
    public bool IsSelected(SsmsObjectExplorerServer server) =>
        _explorer is not null
            ? string.Equals(server.RootUrn, _explorer.RootUrn, StringComparison.Ordinal)
            : IsSelected(server.ServerName);

    /// <summary>
    /// 這一筆結果與作用中的查詢視窗落在同一台伺服器上。
    /// </summary>
    /// <remarks>
    /// 這是移至定義「新視窗沿用得到的那條連線對不對」的問題，與範圍選了哪一台無關：
    /// 使用者從物件總管選的，十之八九正是查詢視窗已經連著的那一台。
    ///
    /// 問的是<b>那一筆</b>的伺服器，不是這一輪範圍的：清單上的舊列可能來自上一台。
    ///
    /// 比對沿用 <see cref="IsSameServer(string?, string?)"/>，與下拉「同一台不列兩次」及
    /// <see cref="ResolveExplorerServer"/> 同一份規則：伺服器名稱的寫法只有一份，
    /// 在這裡另寫一套的症狀是下拉說同一台、這一支說不同台。
    ///
    /// <b>只問伺服器，不問資料庫。</b>新視窗沿用的是連線，而一份結果清單本來就跨資料庫；
    /// 指令碼自己帶著它該去的那一個。要求連同資料庫一致等於把跨資料庫的結果整批擋掉。
    /// </remarks>
    public bool SharesActiveEditorServer(SqlSearchOrigin origin)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        return IsSameServer(origin.ServerName, ActiveEditorServerName());
    }

    /// <summary>選物件總管上的一台。</summary>
    /// <returns>真的換了才回 true，呼叫端據此決定要不要重搜。</returns>
    public bool Select(SsmsObjectExplorerServer server)
    {
        if (server is null) throw new ArgumentNullException(nameof(server));

        if (_explorer is not null && string.Equals(server.RootUrn, _explorer.RootUrn, StringComparison.Ordinal))
        {
            // 同一台重新列出來的新物件；換了等於把目錄丟掉重建一輪，而內容一模一樣。
            _explorer = server;
            return false;
        }

        _explorer = server;
        _selected = null;
        return true;
    }

    /// <summary>選從查詢視窗抓下來的那條連線（見 <see cref="ReadActiveEditorAsync"/>）。</summary>
    /// <returns>真的換了才回 true。</returns>
    public bool Select(SqlSearchConnection connection)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        if (_explorer is null && _selected is not null && SqlSearchConnection.SameScope(_selected, connection)) return false;

        _explorer = null;
        _selected = connection;
        return true;
    }

    /// <summary>
    /// 作用中查詢視窗現在的目錄與伺服器；沒有視窗或沒有連線時為 null。不改變選擇。
    /// </summary>
    /// <remarks>
    /// 先讓中繼資料服務確認現在連到哪裡：SSMS 的連線事件只在服務上立旗標，真的去問在背景
    /// （見 <c>SqlEditorConnectionWatcher</c>）。不等的話，剛換過連線的查詢視窗交出來的是
    /// 上一台的目錄，而那就是使用者接下來每一輪都在搜的範圍。
    /// </remarks>
    public async Task<SqlSearchConnection?> ReadActiveEditorAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (ActiveSqlEditor.Current is not { } view) return null;

        await SqlCompletionServices.GetMetadataService(view, _services).ConfirmConnectionAsync().ConfigureAwait(true);
        return ActiveEditorConnection();
    }

    /// <summary>
    /// 物件總管上已連線的 SQL Server；取不到物件總管時回傳 null。
    /// </summary>
    /// <remarks>
    /// null 與空清單要分開說：前者是「問不到物件總管」，後者是「上面沒有 SQL Server」。
    /// 合成一種的症狀是使用者看到一份看起來完整的清單，卻不知道少了幾台。
    /// </remarks>
    public IReadOnlyList<SsmsObjectExplorerServer>? ListServers()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return SsmsObjectExplorer.TryList(_services);
    }

    /// <summary>
    /// 作用中查詢視窗連到哪一台；沒有視窗或沒有連線時為 null。
    /// </summary>
    /// <remarks>
    /// 走既有的 <see cref="SqlWindowConnections.ReadActive"/>，不自己再解析一次連線字串：
    /// 伺服器名稱的同義字（<c>Data Source</c>／<c>Server</c>／<c>Addr</c>…）只有那一份名單，
    /// 在這裡另寫一套的症狀是同一台伺服器在下拉裡列兩次。
    /// </remarks>
    public string? ActiveEditorServerName()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var server = SqlWindowConnections.ReadActive(_services)?.Server;
        return string.IsNullOrEmpty(server) ? null : server;
    }

    /// <summary>
    /// 選的是物件總管上的一台，而它已經不在這份清單裡：放掉它，回到還沒選。
    /// </summary>
    /// <remarks>
    /// <paramref name="servers"/> 為 null（問不到物件總管）時<b>不動</b>選擇：那一刻分不出
    /// 「這台斷了」與「物件總管還沒載入」，而把使用者選的範圍默默換掉比留著更糟。
    /// 從查詢視窗抓下來的那一台不看物件總管：它本來就可能不在樹上。
    /// </remarks>
    /// <returns>真的放掉了才回 true。</returns>
    public bool DropMissingServer(IReadOnlyList<SsmsObjectExplorerServer>? servers)
    {
        if (_explorer is null || servers is null) return false;

        foreach (var server in servers)
        {
            if (string.Equals(server.RootUrn, _explorer.RootUrn, StringComparison.Ordinal)) return false;
        }

        _explorer = null;
        _selected = null;
        return true;
    }

    /// <summary>
    /// 這一筆結果在物件總管的哪一台上；樹上沒有那一台時回傳 null。
    /// </summary>
    /// <remarks>
    /// 照那一筆的伺服器找，不照現在的範圍：範圍換過之後，舊列的伺服器仍然是上一台。
    /// 找不到時<b>禁止</b>拿樹上任何一台頂替（包括選定的那一台）：頂替的症狀是導航跳到
    /// 另一台伺服器上同名的物件，而畫面上看起來完全正常。挑法見 <see cref="FindOnTree"/>。
    ///
    /// 只在使用者按下導航那一刻呼叫：列伺服器會取用物件總管服務，而那一步會把它的視窗
    /// 叫出來，理由見 <see cref="SsmsObjectExplorer"/>。
    /// </remarks>
    public SsmsObjectExplorerServer? ResolveExplorerServer(SqlSearchOrigin origin)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        return FindOnTree(_explorer, ListServers(), origin);
    }

    /// <summary>
    /// 這一輪範圍的目錄與它連著的那一台；還沒選、連不上或說不出是哪一台時為 null。
    /// </summary>
    /// <remarks>
    /// 只交出<b>目錄</b>，不交連線來源（見型別註解）。選定的那一台連不上時回 null，
    /// <b>不</b>退回查詢視窗那一台：退回去的答案看起來完全正常，只是來自另一台伺服器。
    /// 說不出伺服器時這一輪不搜——沒有伺服器的命中，下游只剩「照目前範圍猜」這條退路。
    /// </remarks>
    public SqlSearchConnection? ResolveConnection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_selected is { } selected) return selected;
        if (_explorer is not { ServerName: { Length: > 0 } name } server) return null;

        var source = SsmsObjectExplorer.TryCreateConnectionSource(_services, server);
        if (source is null) return null;

        // 交出去之後就不再持有來源，只留目錄；註冊表已經有同一個快取鍵的目錄時，
        // 這一份會被當成重複的釋放掉，留著它等於留一個已釋放的物件。
        return _selected = new SqlSearchConnection(
            SqlMetadataCatalogRegistry.Default.GetOrCreate(source), new SqlSearchOrigin(name));
    }

    /// <summary>這一輪的目錄；沒有連線時為 null。只要目錄的呼叫端（資料庫清單）用它。</summary>
    public SqlMetadataCatalog? Resolve() => ResolveConnection()?.Catalog;

    /// <summary>
    /// 在 <paramref name="origin"/> 那一台上的目錄；範圍已經不在那一台時回傳 null，
    /// 並以 <paramref name="elsewhere"/> 說出是這個原因。
    /// </summary>
    /// <remarks>
    /// 目錄只有範圍那一份，所以這一支<b>拒絕</b>而不去別處找：拿範圍那一台的目錄回答另一台
    /// 的 <c>object_id</c>，問到的是剛好同號的另一個物件（或它的父物件），而那份答案看起來
    /// 完全正常。<paramref name="elsewhere"/> 與「連不上」分開說，兩句的下一步不同：
    /// 一句是重新搜尋，另一句是去看連線。
    /// </remarks>
    public SqlMetadataCatalog? ResolveOn(SqlSearchOrigin origin, out bool elsewhere)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        // 比的是這份目錄自己那一台，不是另外問來的名字：兩者分開問的話，名字與目錄
        // 可能不是同一次解析的，同號的另一個物件就會混進來。
        var scope = ResolveConnection();
        elsewhere = scope is not null && !IsSameServer(origin.ServerName, scope.Origin.ServerName);
        return elsewhere ? null : scope?.Catalog;
    }

    /// <summary>
    /// 這一筆結果指向的那個物件要用哪一份目錄；範圍已經不在它那一台時回傳 null。
    /// </summary>
    /// <remarks>
    /// 伺服器先照 <see cref="ResolveOn"/> 確認，再換資料庫。換目錄的規則只有
    /// <see cref="SqlMetadataCatalogRegistry.ScopeTo(SqlMetadataCatalog?, SqlObjectInfo?)"/>
    /// 一份，與查詢視窗那條路徑共用：一份結果清單本來就跨資料庫，而
    /// <c>object_id</c> 只在它自己那個資料庫裡唯一。
    /// </remarks>
    public SqlMetadataCatalog? ResolveFor(SqlObjectInfo objectInfo, SqlSearchOrigin origin, out bool elsewhere)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (objectInfo is null) throw new ArgumentNullException(nameof(objectInfo));

        return SqlMetadataCatalogRegistry.Default.ScopeTo(ResolveOn(origin, out elsewhere), objectInfo);
    }

    /// <remarks>
    /// 中繼資料服務是每個查詢視窗一份，連線也在那裡；沒有查詢視窗就沒有目錄可抓。
    /// </remarks>
    private SqlSearchConnection? ActiveEditorConnection() => SqlAssistPlatformGuard.Probe<SqlSearchConnection?>(
        "取得查詢視窗的目錄",
        () => ActiveSqlEditor.Current is { } view &&
              SqlCompletionServices.GetMetadataService(view, _services).PeekCurrentConnection() is { } current &&
              current.Server.Length > 0
            ? new SqlSearchConnection(current.Catalog, new SqlSearchOrigin(current.Server))
            : null,
        fallback: null);
}
