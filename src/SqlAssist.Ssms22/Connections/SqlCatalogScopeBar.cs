using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlAssist.Core.Connections;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Notifications;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Connections;

/// <summary>範圍列換了什麼；宿主據此決定要不要整份重讀。</summary>
internal enum SqlCatalogScopeChange
{
    /// <summary>伺服器或整個目錄換了（含套用查詢視窗的連線）：上一份結果不代表這一台了。</summary>
    Catalog,

    /// <summary>同一台上換了資料庫的勾選。</summary>
    Databases
}

/// <summary>宿主那一份資料庫下拉的寫法；伺服器那一顆每個宿主都一樣，不在這裡。</summary>
/// <param name="MultipleDatabases">可以勾好幾個、沒勾就是「全部」（SQL Search）；false 是單選而且必選（資料字典）。</param>
/// <param name="DatabaseHint">每一個資料庫那一列的說明：選它在這個宿主上代表什麼。</param>
/// <param name="AllDatabasesHint">多選時第一列「全部」的說明；單選時不用。</param>
/// <param name="NotificationKind">套用查詢視窗連線失敗時那則通知歸哪一類。</param>
internal sealed record SqlCatalogScopeOptions(
    bool MultipleDatabases,
    string DatabaseHint,
    string AllDatabasesHint,
    NotificationKind NotificationKind);

/// <summary>
/// 範圍列的第一群：套用查詢視窗連線、伺服器、資料庫三顆，SQL Search 與資料字典共用這一份。
/// </summary>
/// <remarks>
/// 三顆的值與規則只有一份：值在 <see cref="SqlConnectionScope"/>，選的是哪一台與它的目錄在
/// <see cref="SqlCatalogSelection"/>，資料庫清單在 <see cref="SqlCatalogDatabases"/>；這裡只把它們接到
/// 兩張 <see cref="SqlFilterFlyout"/> 與一顆套用按鈕上。各工具窗各接一次的症狀是同一條規則
/// （換伺服器清掉資料庫、選的那一台斷了就回到還沒選、問不到清單時留住上一份）只有其中一扇對。
///
/// 範圍只在使用者換它的時候換：切分頁、在查詢視窗換連線都不動它，只有勾選與套用按鈕會換。
/// 唯一的例外是<b>還沒選伺服器</b>時自動套用一次查詢視窗那一條，由宿主在開窗與切分頁時呼叫
/// <see cref="EnsureServerAsync"/>；選定之後就不再跟。
///
/// 全部方法都只能在 UI 執行緒上呼叫。
/// </remarks>
internal sealed class SqlCatalogScopeBar : IDisposable
{
    /// <summary>範圍列只接單選伺服器：一份目錄對一台，多台要的是每台一份清單與一份完整度。</summary>
    [Localizable(false)]
    private const string SingleServerRequired = "範圍列只接單選伺服器、而且資料庫的單複選要與選項一致的範圍。";

    private readonly IServiceProvider _services;
    private readonly SqlCatalogScopeOptions _options;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public SqlCatalogScopeBar(IServiceProvider services, SqlConnectionScope scope, SqlCatalogScopeOptions options)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (scope.MultipleServers) throw new ArgumentException(SingleServerRequired, nameof(scope));
        if (scope.MultipleDatabases != options.MultipleDatabases) throw new ArgumentException(SingleServerRequired, nameof(options));

        Selection = new SqlCatalogSelection(services);
        ServerFlyout = new SqlFilterFlyout(SqlKindText.Server, SqlIcon.Server, SqlFilterMode.Single);
        DatabaseFlyout = new SqlFilterFlyout(SqlKindText.Database, SqlIcon.Database,
            options.MultipleDatabases ? SqlFilterMode.SearchableMultiple : SqlFilterMode.Single);
        ConnectionButton = SqlAssistChrome.CreateEditorConnectionButton(ReadEditorConnection);
        _dispatcher = ConnectionButton.Dispatcher;

        ConnectionButton.Click += (_, _) => Start(() => ApplyEditorConnectionAsync(automatic: false));
        ConfigureServer();
        ConfigureDatabases();
        UpdateSummaries();
    }

    /// <summary>範圍換了；宿主照 <see cref="SqlCatalogScopeChange"/> 重讀。</summary>
    public event EventHandler<SqlCatalogScopeChange>? Changed;

    public SqlConnectionScope Scope { get; }

    public SqlCatalogSelection Selection { get; }

    /// <summary>手上那一份資料庫清單；SQL Search 用它判斷「全部」那一輪要不要先建索引。</summary>
    public SqlCatalogDatabases Databases { get; } = new();

    public Button ConnectionButton { get; }

    public SqlFilterFlyout ServerFlyout { get; }

    public SqlFilterFlyout DatabaseFlyout { get; }

    /// <summary>工具列第一群的三顆，依序排；分隔線由工具列補。</summary>
    public IReadOnlyList<FrameworkElement> Controls => new FrameworkElement[] { ConnectionButton, ServerFlyout, DatabaseFlyout };

    /// <summary>
    /// 把選定那一台的目錄與跟著它走的狀態同步一次；換範圍、開窗與每一輪讀取前都走這一支。
    /// </summary>
    /// <remarks>
    /// 物件總管那一台的目錄在第一次解析時才建，連不上的那一次下一輪會再試。資料庫清單在這裡就
    /// 同步：展開下拉時才比對的話，換一台之後清單仍是上一台的，下拉會一直畫著上一台的資料庫。
    /// </remarks>
    public SqlCatalogConnection? Observe()
    {
        var connection = Selection.ResolveConnection();
        Databases.SyncTo(connection?.Catalog);
        // 伺服器下拉一律可按：連不上目前這一台時，換一台正是使用者要做的事。
        DatabaseFlyout.IsEnabled = connection is not null;
        return connection;
    }

    /// <summary>
    /// 選定的那一個資料庫的目錄；資料庫單選而且還沒選、或沒有連線時為 null。
    /// </summary>
    /// <remarks>
    /// 換資料庫換的是目錄，規則只有 <see cref="SqlMetadataCatalogRegistry.GetOrCreateFor(ISqlConnectionSource, string)"/>
    /// 一份：與補全的跨資料庫同一個快取鍵，查詢視窗連著同一個資料庫時拿到的是同一份目錄。
    /// </remarks>
    public SqlMetadataCatalog? ResolveDatabaseCatalog(SqlCatalogConnection? connection)
    {
        if (connection is null || Scope.MultipleDatabases || Scope.Databases.Count == 0) return null;

        var database = Scope.Databases[0];
        var catalog = connection.Catalog;
        return string.Equals(catalog.ConnectionSource.DatabaseName, database, StringComparison.OrdinalIgnoreCase)
            ? catalog
            : SqlMetadataCatalogRegistry.Default.GetOrCreateFor(catalog.ConnectionSource, database);
    }

    /// <summary>「重新整理」：資料庫清單標成舊的，下一次展開重問；剛建好的資料庫正是使用者按它的理由。</summary>
    public void Refresh() => Databases.Invalidate();

    /// <summary>打開伺服器面板；狀態表面「選擇伺服器」那顆按鈕用。</summary>
    public void OpenServers() => ServerFlyout.Open();

    /// <summary>打開資料庫面板；資料庫必選而還沒選時，狀態表面那顆按鈕用。</summary>
    public void OpenDatabases() => DatabaseFlyout.Open();

    /// <summary>還沒選伺服器時自動套用查詢視窗那一條；開窗與切分頁時由宿主呼叫。</summary>
    public Task EnsureServerAsync() =>
        Selection.ServerName is null ? ApplyEditorConnectionAsync(automatic: true) : Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Databases.Dispose();
    }

    /// <summary>兩顆下拉的摘要與強調底框；換了任何一個值都要重畫。</summary>
    private void UpdateSummaries()
    {
        ServerFlyout.UpdateSummary(SqlCatalogScopeSummary.Server(Scope), "");
        DatabaseFlyout.UpdateSummary(SqlCatalogScopeSummary.Databases(Scope), SqlFilterSummary.Detail(Scope.Databases));

        // 選了值的維度換強調底框；伺服器必選，選定之後一樣亮，否則一排按鈕裡只有每一輪都在用的
        // 那一台看起來像還沒設定。
        ServerFlyout.HasSelection = Scope.Servers.Count != 0;
        DatabaseFlyout.HasSelection = Scope.Databases.Count != 0;
    }

    private void Raise(SqlCatalogScopeChange change)
    {
        if (_disposed) return;
        UpdateSummaries();
        Changed?.Invoke(this, change);
    }

    /// <summary>
    /// 伺服器單選：目前連得上的每一台——物件總管上已連線的，加上查詢視窗連著的那一台。
    /// </summary>
    /// <remarks>
    /// 清單只在使用者打開下拉那一刻重問（沒有 I/O，見 <see cref="SsmsObjectExplorer"/>）。
    /// <b>禁止</b>改成輪詢：物件總管服務第一次取用會把那個工具視窗叫出來，
    /// 使用者把它關掉之後，輪詢會在他沒有要求的時候替他開回去。
    /// </remarks>
    private void ConfigureServer()
    {
        VsThemeBrushes.Apply(ServerFlyout);
        ServerFlyout.OptionsRequested += (_, _) => Run(FillServer);
    }

    /// <summary>
    /// 伺服器下拉：一台一行，沒有「查詢視窗」這種會跟著分頁變的選項。
    /// </summary>
    /// <remarks>
    /// 查詢視窗連著的那一台不在物件總管上時也列出來：套用按鈕套得到的，面板上就要選得到。
    /// 同一台不列兩次，比對走連線字串裡的伺服器名稱（<see cref="SqlCatalogSelection.IsSameServer(string?, string?)"/>），
    /// 不是快取鍵——快取鍵是整串正規化過的連線字串，同一台伺服器的兩條連線幾乎不會相等。
    /// 目前選的那一台（從查詢視窗套用、而那個分頁已經換掉）兩處都沒有時照樣列出來，
    /// 否則面板上看不出現在選的是哪一台。
    ///
    /// 問不到物件總管時<b>明說</b>，不假裝這就是全部：少列一台而使用者看不出差別，
    /// 比只列一台更糟。
    /// </remarks>
    private void FillServer()
    {
        var servers = Selection.ListServers();

        // 選的那一台已經從物件總管上消失了（使用者中斷了連線）：回到還沒選，
        // 而不是留著一個連不上的範圍讓每一輪都空手而回。
        if (Selection.DropMissingServer(servers)) ServerChanged();

        var editorServer = Selection.ActiveEditorServerName();
        var listed = new List<string?>();
        var options = new List<SqlFilterOption>();

        void Add(string label, string? serverName, Func<bool> isSelected, string hint, Action select)
        {
            listed.Add(serverName);
            // 單選：勾掉等於沒有範圍可用，所以只理會「選這一個」。
            options.Add(new SqlFilterOption(label, hint, isSelected(),
                value => Run(() => { if (value && !isSelected()) select(); })));
        }

        foreach (var server in servers ?? Array.Empty<SsmsObjectExplorerServer>())
        {
            Add(server.DisplayName, server.ServerName, () => Selection.IsSelected(server),
                ConnectionText.ExplorerServerDescription, () => { if (Selection.Select(server)) ServerChanged(); });
        }

        if (editorServer is not null && !listed.Exists(name => SqlCatalogSelection.IsSameServer(name, editorServer)))
        {
            Add(editorServer, editorServer, () => Selection.IsSelected(editorServer),
                ConnectionText.EditorServerDescription, () => Start(SelectEditorServerAsync));
        }

        if (Selection.ServerName is { } current && !listed.Exists(Selection.IsSelected))
        {
            Add(current, current, () => true, ConnectionText.CurrentServerDescription, () => { });
        }

        ServerFlyout.SetOptions(new[] { new SqlFilterGroup("", options) });
        ServerFlyout.SetNotice(servers is null ? ConnectionText.ExplorerUnavailable : "");
    }

    /// <summary>查詢視窗現在的連線；沒有視窗或沒有連線時為 null。只給套用按鈕的 Tooltip 用。</summary>
    private SqlConnectionLabel? ReadEditorConnection() =>
        SqlAssistPlatformGuard.Probe("取得查詢視窗連線", () => SqlWindowConnections.ReadActive(_services), null);

    /// <summary>
    /// 套用查詢視窗的連線：伺服器換成它連著的那一台，資料庫換成它連著的那一個。
    /// </summary>
    /// <param name="automatic">
    /// 還沒選伺服器時自動補的那一次：查詢視窗沒有連線就安靜地什麼都不做，而且等的途中使用者
    /// 自己挑了一台就不蓋掉他。
    /// </param>
    /// <remarks>
    /// 與 SQL Memory 那一顆同一條規則（<see cref="SqlConnectionScope.Apply"/>）：取代而不是加進去，
    /// 之後切分頁、換連線都不再跟著走。用的是查詢視窗<b>那條連線</b>
    /// （<see cref="SqlCatalogSelection.ReadActiveEditorAsync"/>），所以物件總管上沒有的那一台也選得到。
    /// 查詢視窗沒有連線時<b>不動</b>範圍。
    /// </remarks>
    private async Task ApplyEditorConnectionAsync(bool automatic)
    {
        var connection = await Selection.ReadActiveEditorAsync().ConfigureAwait(true);
        if (_disposed || (automatic && Selection.ServerName is not null)) return;

        var label = connection is null
            ? null
            : new SqlConnectionLabel(connection.ServerName, connection.Catalog.ConnectionSource.DatabaseName);

        if (connection is null || !Scope.Apply(label))
        {
            if (!automatic) NotifyNotConnected();
            return;
        }

        Selection.Select(connection);
        CatalogChanged();
    }

    /// <summary>按了套用、查詢視窗卻沒有連線；範圍沒動，說一句為什麼沒反應。</summary>
    private void NotifyNotConnected() =>
        NotificationCenter.Default.Post(NotificationCatalog.ApplyingEditorConnection, _options.NotificationKind,
            NotificationOrigin.User, NotificationLevel.Info, NotificationStatus.Failed, "",
            message: SqlEditorConnectionText.NotConnectedReport);

    /// <summary>從伺服器下拉選查詢視窗連著的那一台（它不在物件總管上）；資料庫跟著清掉。</summary>
    private async Task SelectEditorServerAsync()
    {
        if (await Selection.ReadActiveEditorAsync().ConfigureAwait(true) is not { } connection)
        {
            NotifyNotConnected();
            return;
        }

        if (!_disposed && Selection.Select(connection)) ServerChanged();
    }

    /// <summary>
    /// 選定的伺服器換了（或放掉了）：值那一份跟著換，資料庫的勾選由它一起清掉。
    /// </summary>
    /// <remarks>
    /// 名稱照 <see cref="SqlCatalogSelection.ServerName"/> 寫回：兩份各自決定的話，摘要說的
    /// 與實際用的會是兩台。
    /// </remarks>
    private void ServerChanged()
    {
        if (Selection.ServerName is { } name) Scope.SetServerSelected(name, selected: true);
        else Scope.ClearServers();

        CatalogChanged();
        // 選的那一台放掉了（從物件總管中斷）：與開窗時同一條規則，還沒選就自動套用查詢視窗那一條。
        if (Selection.ServerName is null) Start(() => ApplyEditorConnectionAsync(automatic: true));
    }

    /// <summary>
    /// 目錄換了：先同步清單，再讓宿主重讀。
    /// </summary>
    /// <remarks>
    /// 兩張面板的重建要等這一輪事件走完：面板的繫結可能還在回寫。伺服器面板只在開著時重畫——
    /// 列伺服器會取用物件總管服務，沒有人在看的時候不去叫它。
    /// </remarks>
    private void CatalogChanged()
    {
        if (_disposed) return;

        Observe();
        Defer(() => FillDatabases(Databases.Items));
        if (ServerFlyout.IsOpen) Defer(FillServer);
        Raise(SqlCatalogScopeChange.Catalog);
    }

    private void ConfigureDatabases()
    {
        VsThemeBrushes.Apply(DatabaseFlyout);
        DatabaseFlyout.OptionsRequested += (_, _) => Start(ShowDatabasesAsync);
        if (!_options.MultipleDatabases) return;

        // 全選等清單到齊才動手：只全選手上那一份的症狀是使用者在清單還在路上時按了它，
        // 而勾起來的是幾個名稱而不是整台。面板上打了字就只勾篩出來的那幾個，而且照面板
        // 同一條比對規則再篩一次——這裡自己寫一份的下場是看到五個、勾起來七個。
        DatabaseFlyout.SelectAllRequested += pattern => Start(async () =>
        {
            var databases = await Databases.EnsureAsync(Selection.Resolve()).ConfigureAwait(true);
            var changed = false;
            foreach (var database in databases)
            {
                if (!SqlFilterFlyout.Matches(database.Name, pattern)) continue;
                changed |= Scope.SetDatabaseSelected(database.Name, selected: true);
            }

            FillDatabases(databases);
            if (changed) Raise(SqlCatalogScopeChange.Databases);
        });
        // 全不選與第一列那個「全部」是同一件事，所以走同一條清除路徑，不另寫一份狀態同步。
        DatabaseFlyout.ClearAllRequested += (_, _) => Run(ClearDatabases);
        DatabaseFlyout.SetBulkCommands(SqlFilterBulkCommands.SelectAndClear);
    }

    /// <summary>清掉整個資料庫維度；第一列那個預設與全不選共用這一份。</summary>
    private void ClearDatabases()
    {
        if (!Scope.ClearDatabases()) return;
        Raise(SqlCatalogScopeChange.Databases);
        // 其餘幾列的勾要一起清掉，但重建整份清單得等這一次的繫結回寫結束：
        // 在回寫途中換掉 ItemsSource 等於把正在發事件的那一顆核取方塊回收掉。
        Defer(() => FillDatabases(Databases.Items));
    }

    /// <summary>
    /// 面板打開了：先畫手上有的，清單還沒到就一邊說一句一邊去問。
    /// </summary>
    /// <remarks>
    /// 展開下拉<b>就是</b>使用者在要求這份清單，所以這裡去問資料庫是對的；禁止的是在沒有人
    /// 打開它的時候先問一輪。問一次留一份，換連線與按重新整理才重問，規則在
    /// <see cref="SqlCatalogDatabases"/>。
    /// </remarks>
    private async Task ShowDatabasesAsync()
    {
        var catalog = Selection.Resolve();

        // 先畫：已經勾起來的條件一定要看得見，否則使用者在等清單的期間取消不掉自己剛選的那一個。
        FillDatabases(Databases.Items);
        if (catalog is null || Databases.IsLoaded) return;

        DatabaseFlyout.SetNotice(ConnectionText.LoadingDatabases, busy: true);
        FillDatabases(await Databases.EnsureAsync(catalog).ConfigureAwait(true));
    }

    /// <summary>
    /// 資料庫下拉的兩段：使用者資料庫與系統資料庫。
    /// </summary>
    /// <remarks>
    /// 已經勾起來的名稱排在最前面且一律列出，就算這一份清單裡沒有它：伺服器回不來或名稱剛被
    /// 卸除時，使用者仍然要取消得掉自己剛選的條件。分段依伺服器說的
    /// <see cref="SqlServerDatabase.IsSystem"/>，只有還沒問到清單的名稱才退回
    /// <see cref="SqlServerDatabase.IsSystemName"/>。
    /// </remarks>
    private void FillDatabases(IReadOnlyList<SqlServerDatabase> databases)
    {
        var user = new List<SqlFilterOption>();
        var system = new List<SqlFilterOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string database, bool isSystem)
        {
            if (!seen.Add(database)) return;
            var option = new SqlFilterOption(
                database,
                _options.DatabaseHint,
                Scope.IsDatabaseSelected(database),
                selected => Run(() =>
                {
                    if (!Scope.SetDatabaseSelected(database, selected)) return;
                    if (Scope.MultipleDatabases) DatabaseFlyout.SyncEmptyOption(Scope.Databases.Count == 0);
                    Raise(SqlCatalogScopeChange.Databases);
                }));
            (isSystem ? system : user).Add(option);
        }

        foreach (var database in Scope.Databases) Add(database, SqlServerDatabase.IsSystemName(database));
        foreach (var database in databases) Add(database.Name, database.IsSystem);

        // 多選的第一列是「全部」，與按鈕摘要共用同一份字；選它等於清掉整個維度，
        // 所以面板上不另畫一顆「清除」。單選必選，沒有這一列。
        DatabaseFlyout.SetEmptyOption(Scope.MultipleDatabases
            ? new SqlFilterOption(
                SqlCatalogScopeSummary.AllDatabasesLabel,
                _options.AllDatabasesHint,
                Scope.Databases.Count == 0,
                // 取消勾它不是一個範圍；面板那一列自己會彈回去，這裡只忽略。
                selected => Run(() => { if (selected) ClearDatabases(); }))
            : null);

        DatabaseFlyout.SetOptions(new[]
        {
            new SqlFilterGroup(ConnectionText.UserDatabases, user),
            new SqlFilterGroup(ConnectionText.SystemDatabases, system)
        });
        DatabaseFlyout.SetNotice(DatabaseNotice(seen.Count));
    }

    /// <summary>
    /// 清單上方那一句；沒有話要說時是空字串。
    /// </summary>
    /// <remarks>
    /// 「問不到」與「這台上一個都進不去」要分開說：前者叫使用者重試或去看權限，後者是答案本身。
    /// 混成一句的症狀是他反覆重開下拉，等一份永遠不會出現的清單。
    /// </remarks>
    private string DatabaseNotice(int listed)
    {
        if (Databases.IsUnavailable)
        {
            return listed == 0 ? ConnectionText.DatabaseListUnavailable : ConnectionText.DatabaseListStale;
        }

        if (!Databases.IsLoaded) return "";
        return listed == 0 ? ConnectionText.NoAccessibleDatabases : "";
    }

    private void Run(Action action) => SqlAssistPlatformGuard.Run("操作範圍列", action);

    private static void Start(Func<Task> action) => _ = SqlAssistPlatformGuard.RunAsync("操作範圍列", action);

    /// <summary>等這一輪事件走完再做；收掉的視窗不補做——那時候畫面已經沒有人在看。</summary>
    private void Defer(Action action) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_disposed) Run(action);
        }));
}
