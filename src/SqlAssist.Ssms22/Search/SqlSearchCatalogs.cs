using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.SqlMemory;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// SQL Search 這一輪要用哪一份中繼資料目錄——清單、預覽與移至定義<b>唯一</b>的出處。
/// </summary>
/// <remarks>
/// 三條路徑各自去問「目錄在哪裡」的話，指名別台伺服器之後只要有一條忘了換，
/// 它就會拿查詢視窗那台伺服器上同號的物件回答，而畫面上看不出跳錯了——
/// <c>object_id</c> 跨伺服器毫無關係，這是整個功能最貴的一種錯。
///
/// 目錄有兩個來源，對上面三條路徑是同一種東西：
/// 沒有指名伺服器時跟著作用中的查詢視窗（既有行為，走
/// <see cref="SqlMetadataService.PeekCurrentCatalog"/>）；指名了就走物件總管那一台。
///
/// <b>禁止</b>持有 <c>ISqlConnectionSource</c>：所有權在
/// <see cref="SqlMetadataCatalogRegistry"/>，同一個快取鍵重複建立時多出來的那一份會當場
/// 釋放，留著的症狀是之後每一輪都以 <see cref="ObjectDisposedException"/> 收場，
/// 而那不是 <see cref="System.Data.Common.DbException"/>，索引那一層的降級接不住。
/// 這裡留的是<b>目錄</b>，與 <see cref="SqlSearchProviders"/> 同一個規矩。
///
/// 全部方法都只能在 UI 執行緒上呼叫：作用中編輯器與物件總管的服務都有 UI 相依性。
/// </remarks>
internal sealed class SqlSearchCatalogs
{
    private readonly IServiceProvider _services;

    /// <summary>指名的伺服器；null 表示跟著作用中的查詢視窗。</summary>
    private SsmsObjectExplorerServer? _server;

    /// <summary>
    /// 指名伺服器時解析出來的那一份目錄。
    /// </summary>
    /// <remarks>
    /// 留著是因為每一輪搜尋、每一次選取列都會問一次，而重建要向物件總管走一趟
    /// <c>FindNode</c> 再配一條連線——那都在 UI 執行緒上。目錄本身是註冊表共用的，
    /// 留一份參考沒有所有權問題；換伺服器與掉線時由 <see cref="Select"/>／
    /// <see cref="DropMissingServer"/> 清掉。
    /// </remarks>
    private SqlMetadataCatalog? _selected;

    internal SqlSearchCatalogs(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>目前指名的伺服器；null 表示跟著作用中的查詢視窗。</summary>
    public SsmsObjectExplorerServer? Server => _server;

    /// <summary>範圍跟著作用中的查詢視窗走，也就是沒有指名伺服器。</summary>
    public bool FollowsActiveEditor => _server is null;

    /// <summary>
    /// 換一台伺服器；<paramref name="server"/> 為 null 表示回到作用中的查詢視窗。
    /// </summary>
    /// <returns>真的換了才回 true，呼叫端據此決定要不要重搜。</returns>
    public bool Select(SsmsObjectExplorerServer? server)
    {
        if (ReferenceEquals(server, _server)) return false;

        if (server is not null && _server is not null &&
            string.Equals(server.RootUrn, _server.RootUrn, StringComparison.Ordinal))
        {
            // 同一台重新列出來的新物件；換了等於把目錄丟掉重建一輪，而內容一模一樣。
            _server = server;
            return false;
        }

        _server = server;
        _selected = null;
        return true;
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
        return SsmsObjectExplorerServers.TryList(_services);
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
    /// 指名的伺服器已經不在這份清單裡就放掉它，回到作用中的查詢視窗。
    /// </summary>
    /// <remarks>
    /// <paramref name="servers"/> 為 null（問不到物件總管）時<b>不動</b>選擇：那一刻分不出
    /// 「這台斷了」與「物件總管還沒載入」，而把使用者選的範圍默默換掉比留著更糟。
    /// </remarks>
    /// <returns>真的放掉了才回 true。</returns>
    public bool DropMissingServer(IReadOnlyList<SsmsObjectExplorerServer>? servers)
    {
        if (_server is null || servers is null) return false;

        foreach (var server in servers)
        {
            if (string.Equals(server.RootUrn, _server.RootUrn, StringComparison.Ordinal)) return false;
        }

        _server = null;
        _selected = null;
        return true;
    }

    /// <summary>
    /// 這一輪的目錄；沒有連線時為 null。
    /// </summary>
    /// <remarks>
    /// 只交出<b>目錄</b>，不交連線來源（見型別註解）。指名的伺服器連不上時回 null 而不是
    /// 退回查詢視窗那一台：退回去的答案看起來完全正常，只是來自另一台伺服器。
    /// </remarks>
    public SqlMetadataCatalog? Resolve()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_server is not { } server) return ActiveEditorCatalog();
        if (_selected is { } cached) return cached;

        var source = SsmsObjectExplorerServers.TryCreateConnectionSource(_services, server);
        if (source is null) return null;

        // 交出去之後就不再持有來源，只留目錄；註冊表已經有同一個快取鍵的目錄時，
        // 這一份會被當成重複的釋放掉，留著它等於留一個已釋放的物件。
        _selected = SqlMetadataCatalogRegistry.Default.GetOrCreate(source);
        return _selected;
    }

    /// <summary>
    /// 這一筆結果指向的那個物件要用哪一份目錄。
    /// </summary>
    /// <remarks>
    /// 換目錄的規則只有 <see cref="SqlMetadataCatalogRegistry.ScopeTo(SqlMetadataCatalog?, SqlObjectInfo?)"/>
    /// 一份，與查詢視窗那條路徑共用：一份結果清單本來就跨資料庫，而
    /// <c>object_id</c> 只在它自己那個資料庫裡唯一。
    /// </remarks>
    public SqlMetadataCatalog? ResolveFor(SqlObjectInfo objectInfo)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (objectInfo is null) throw new ArgumentNullException(nameof(objectInfo));

        return SqlMetadataCatalogRegistry.Default.ScopeTo(Resolve(), objectInfo);
    }

    /// <remarks>
    /// 中繼資料服務是每個查詢視窗一份，連線也在那裡；沒有查詢視窗就沒有目錄可問，
    /// 而那時候使用者要看的是「去連線」而不是一份空清單。
    /// </remarks>
    private SqlMetadataCatalog? ActiveEditorCatalog() => SqlAssistPlatformGuard.Probe<SqlMetadataCatalog?>(
        "取得 SQL Search 的目錄",
        () =>
        {
            var view = ActiveSqlEditor.Current;
            return view is null ? null : SqlCompletionServices.GetMetadataService(view, _services).PeekCurrentCatalog();
        },
        fallback: null);
}
