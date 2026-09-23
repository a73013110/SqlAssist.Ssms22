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
/// 三條路徑各自去問「目錄在哪裡」的話，指名別台伺服器之後只要有一條忘了換，
/// 它就會拿查詢視窗那台伺服器上同號的物件回答，而畫面上看不出跳錯了——
/// <c>object_id</c> 跨伺服器毫無關係，這是整個功能最貴的一種錯。
///
/// 目錄有兩個來源，對上面三條路徑是同一種東西：
/// 沒有指名伺服器時跟著作用中的查詢視窗（既有行為，走
/// <see cref="SqlMetadataService.PeekCurrentConnection"/>）；指名了就走物件總管那一台。
/// 兩條都連同伺服器一起交出（<see cref="SqlSearchConnection"/>），不另外問一次。
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
/// 清單比範圍活得久：換了查詢視窗或指名別台之後，舊的那幾列仍然指向上一台，而現在的
/// 範圍答的是換過之後那一台。所以導航、沿用連線與目錄查詢三條路都把那一筆的伺服器帶進來問，
/// 範圍不在那一台時照實拒絕，不拿現在這一台同名、同號的東西代答。
/// 純判斷那一半（比對規則、在樹上挑哪一台）在 <c>SqlSearchCatalogs.Servers.cs</c>，零 VS 相依。
/// </remarks>
internal sealed partial class SqlSearchCatalogs
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
    /// <remarks>
    /// 這是<b>範圍的狀態</b>：下拉裡哪一列打勾、按鈕上的摘要說跟著誰，讀的都是它。
    /// 「新視窗沿用得到的那條連線對不對」是另一個問題，走
    /// <see cref="SharesActiveEditorServer"/>。
    /// </remarks>
    public bool FollowsActiveEditor => _server is null;

    /// <summary>
    /// 這一筆結果與作用中的查詢視窗落在同一台伺服器上。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="FollowsActiveEditor"/> 是<b>兩個</b>問題，而且常常答案不同：使用者
    /// 從物件總管指名的，十之八九正是查詢視窗已經連著的那一台。拿「有沒有指名」代答的
    /// 症狀就是那個情形——兩邊明明同一台，移至定義卻回一句「請先把查詢視窗連到那一台」，
    /// 而使用者看著自己剛連好的視窗，沒有任何辦法讓它閉嘴。
    ///
    /// 問的是<b>那一筆</b>的伺服器，不是這一輪範圍的：範圍跟著查詢視窗時，換過視窗之後
    /// 「範圍與視窗同一台」恆真，而清單上的舊列來自上一台。
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
    /// 這一筆結果在物件總管的哪一台上；樹上沒有那一台時回傳 null。
    /// </summary>
    /// <remarks>
    /// 照那一筆的伺服器找，不照現在的範圍：範圍換過之後，舊列的伺服器仍然是上一台。
    /// 找不到時<b>禁止</b>拿樹上任何一台頂替（包括指名的那一台）：頂替的症狀是導航跳到
    /// 另一台伺服器上同名的物件，而畫面上看起來完全正常。挑法見 <see cref="FindOnTree"/>。
    ///
    /// 只在使用者按下導航那一刻呼叫：列伺服器會取用物件總管服務，而那一步會把它的視窗
    /// 叫出來，理由見 <see cref="SsmsObjectExplorer"/>。
    /// </remarks>
    public SsmsObjectExplorerServer? ResolveExplorerServer(SqlSearchOrigin origin)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        return FindOnTree(_server, ListServers(), origin);
    }

    /// <summary>
    /// 這一輪範圍的目錄與它連著的那一台；沒有連線或說不出是哪一台時為 null。
    /// </summary>
    /// <remarks>
    /// 只交出<b>目錄</b>，不交連線來源（見型別註解）。指名的伺服器連不上時回 null 而不是
    /// 退回查詢視窗那一台：退回去的答案看起來完全正常，只是來自另一台伺服器。
    /// 說不出伺服器時這一輪不搜——沒有伺服器的命中，下游只剩「照目前範圍猜」這條退路。
    ///
    /// 跟著查詢視窗時交出的是中繼資料服務<b>手上</b>那一份，換連線之後可能還是上一台的
    /// （仍然一致：目錄與伺服器同是上一台）。要最新的，先等 <see cref="ConfirmAsync"/>。
    /// </remarks>
    public SqlSearchConnection? ResolveConnection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_server is not { } server) return ActiveEditorConnection();
        if (server.ServerName is not { Length: > 0 } name) return null;
        if (_selected is not { } catalog)
        {
            var source = SsmsObjectExplorer.TryCreateConnectionSource(_services, server);
            if (source is null) return null;

            // 交出去之後就不再持有來源，只留目錄；註冊表已經有同一個快取鍵的目錄時，
            // 這一份會被當成重複的釋放掉，留著它等於留一個已釋放的物件。
            catalog = _selected = SqlMetadataCatalogRegistry.Default.GetOrCreate(source);
        }

        return new SqlSearchConnection(catalog, new SqlSearchOrigin(name));
    }

    /// <summary>這一輪的目錄；沒有連線時為 null。只要目錄的呼叫端（資料庫清單）用它。</summary>
    public SqlMetadataCatalog? Resolve() => ResolveConnection()?.Catalog;

    /// <summary>
    /// 跟著查詢視窗時，先讓中繼資料服務確認現在連到哪裡；指名物件總管那一台時沒有要確認的。
    /// </summary>
    /// <remarks>
    /// SSMS 的連線事件只在服務上立旗標，真的去問在背景（見 <c>SqlEditorConnectionWatcher</c>）。
    /// 搜尋等得起，所以每一輪與每一次連線變更都先等這一步：不等的話，換連線之後的第一輪
    /// 搜的是上一台，而之後沒有任何事件會再叫它重搜。剛開的查詢視窗同理——服務還沒解析出目錄，
    /// 工具窗會一直說「尚未連線」。不必確認時不開工作，直接完成。
    /// </remarks>
    public async Task ConfirmAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (_server is not null || ActiveSqlEditor.Current is not { } view) return;

        await SqlCompletionServices.GetMetadataService(view, _services).ConfirmConnectionAsync().ConfigureAwait(true);
    }

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

        // 比的是這份目錄自己那一台，不是另外問來的名字：兩者分開問，換連線的那一段裡
        // 名字已經是新的那一台，目錄卻還是上一台，同號的另一個物件就會混進來。
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
    /// 中繼資料服務是每個查詢視窗一份，連線也在那裡；沒有查詢視窗就沒有目錄可問，
    /// 而那時候使用者要看的是「去連線」而不是一份空清單。
    /// </remarks>
    private SqlSearchConnection? ActiveEditorConnection() => SqlAssistPlatformGuard.Probe<SqlSearchConnection?>(
        "取得 SQL Search 的目錄",
        () => ActiveSqlEditor.Current is { } view &&
              SqlCompletionServices.GetMetadataService(view, _services).PeekCurrentConnection() is { } current &&
              current.Server.Length > 0
            ? new SqlSearchConnection(current.Catalog, new SqlSearchOrigin(current.Server))
            : null,
        fallback: null);
}
