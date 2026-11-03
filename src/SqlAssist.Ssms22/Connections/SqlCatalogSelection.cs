using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Metadata.Caching;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 範圍列選的是哪一台伺服器、用哪一份中繼資料目錄——SQL Search 與資料字典<b>唯一</b>的出處。
/// </summary>
/// <remarks>
/// 一個視窗裡好幾條路徑各自去問「目錄在哪裡」的話，換了伺服器之後只要有一條忘了換，
/// 它就會拿另一台伺服器上同號的物件回答，而畫面上看不出跳錯了——
/// <c>object_id</c> 跨伺服器毫無關係。
///
/// 伺服器是使用者<b>明確選的</b>一台，沒有「跟著查詢視窗」這種會自己變的狀態：切分頁、在查詢視窗
/// 換連線都不動它。來源有兩種，對下游是同一種東西：物件總管上已連線的一台
/// （<see cref="Select(SsmsObjectExplorerServer)"/>），或從查詢視窗抓下來的那條連線
/// （<see cref="ReadActiveEditorAsync"/> 再 <see cref="Select(SqlCatalogConnection)"/>）。後者讓物件總管上
/// 沒有的那一台也選得到；抓下來的是註冊表裡的編輯器目錄，它不會被淘汰，而連線來源是複製出來的
/// 樣板，查詢視窗關掉之後照樣可用。兩種都連同伺服器一起交出（<see cref="SqlCatalogConnection"/>），
/// 不另外問一次。
///
/// <b>禁止</b>持有 <c>ISqlConnectionSource</c>：所有權在
/// <see cref="SqlMetadataCatalogRegistry"/>，同一個快取鍵重複建立時多出來的那一份會當場
/// 釋放，留著的症狀是之後每一輪都以 <see cref="ObjectDisposedException"/> 收場，
/// 而那不是 <see cref="System.Data.Common.DbException"/>，降級接不住。這裡留的是<b>目錄</b>。
///
/// 全部方法都只能在 UI 執行緒上呼叫：作用中編輯器與物件總管的服務都有 UI 相依性。
/// 純判斷那一半（伺服器名稱怎麼比）在 <c>SqlCatalogSelection.Servers.cs</c>，零 VS 相依。
/// </remarks>
internal sealed partial class SqlCatalogSelection
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
    private SqlCatalogConnection? _selected;

    internal SqlCatalogSelection(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>選的是物件總管上的哪一台；導航在樹上找那一台時優先用它。</summary>
    public SsmsObjectExplorerServer? Server => _explorer;

    /// <summary>選定那一台叫什麼；還沒選時為 null。按鈕摘要與連不上時那一句都用它。</summary>
    public string? ServerName => _explorer?.DisplayName ?? _selected?.ServerName;

    /// <summary>
    /// 選定的就是這一台；比的是伺服器名稱，不管它是從物件總管還是查詢視窗選來的。
    /// </summary>
    /// <remarks>
    /// 同一台在下拉裡只列一行，所以勾選照名稱畫：從查詢視窗套用的那一台就是物件總管上
    /// 那一行，而使用者再點一次它不該換掉查詢視窗那條連線（登入可能不同）。
    /// </remarks>
    public bool IsSelected(string? serverName) =>
        IsSameServer(serverName, _explorer?.ServerName ?? _selected?.ServerName);

    /// <summary>選定的就是物件總管上這一行。</summary>
    /// <remarks>
    /// 選的是物件總管上的一台時比節點：同一台在樹上可能有兩條連線（不同登入），照名稱比的話兩行都打勾。
    /// 選的是查詢視窗那條連線時照名稱，理由見 <see cref="IsSelected(string?)"/>。
    /// </remarks>
    public bool IsSelected(SsmsObjectExplorerServer server) =>
        _explorer is not null
            ? string.Equals(server.RootUrn, _explorer.RootUrn, StringComparison.Ordinal)
            : IsSelected(server.ServerName);

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
    public bool Select(SqlCatalogConnection connection)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        if (_explorer is null && _selected is not null && SqlCatalogConnection.SameScope(_selected, connection)) return false;

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
    public async Task<SqlCatalogConnection?> ReadActiveEditorAsync()
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
    /// 這一輪範圍的目錄與它連著的那一台；還沒選、連不上或說不出是哪一台時為 null。
    /// </summary>
    /// <remarks>
    /// 只交出<b>目錄</b>，不交連線來源（見型別註解）。選定的那一台連不上時回 null，
    /// <b>不</b>退回查詢視窗那一台：退回去的答案看起來完全正常，只是來自另一台伺服器。
    /// 說不出伺服器時這一輪不搜——沒有伺服器的命中，下游只剩「照目前範圍猜」這條退路。
    /// </remarks>
    public SqlCatalogConnection? ResolveConnection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_selected is { } selected) return selected;
        if (_explorer is not { ServerName: { Length: > 0 } name } server) return null;

        var source = SsmsObjectExplorer.TryCreateConnectionSource(_services, server);
        if (source is null) return null;

        // 交出去之後就不再持有來源，只留目錄；註冊表已經有同一個快取鍵的目錄時，
        // 這一份會被當成重複的釋放掉，留著它等於留一個已釋放的物件。
        return _selected = new SqlCatalogConnection(
            SqlMetadataCatalogRegistry.Default.GetOrCreate(source), name);
    }

    /// <summary>選定那一台的目錄；沒有連線時為 null。只要目錄的呼叫端（資料庫清單）用它。</summary>
    public SqlMetadataCatalog? Resolve() => ResolveConnection()?.Catalog;

    /// <remarks>
    /// 中繼資料服務是每個查詢視窗一份，連線也在那裡；沒有查詢視窗就沒有目錄可抓。
    /// </remarks>
    private SqlCatalogConnection? ActiveEditorConnection() => SqlAssistPlatformGuard.Probe<SqlCatalogConnection?>(
        "取得查詢視窗的目錄",
        () => ActiveSqlEditor.Current is { } view &&
              SqlCompletionServices.GetMetadataService(view, _services).PeekCurrentConnection() is { } current &&
              current.Server.Length > 0
            ? new SqlCatalogConnection(current.Catalog, current.Server)
            : null,
        fallback: null);
}
