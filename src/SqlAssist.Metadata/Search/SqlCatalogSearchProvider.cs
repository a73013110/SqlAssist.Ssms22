using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 目錄物件搜尋：物件名、資料行名與定義本文。
/// </summary>
/// <remarks>
/// 掃的是 <see cref="SqlCatalogSearchIndex"/>，不是按鍵路徑上的那份分層快取；兩者為什麼
/// 分開見那個型別。這一層做四件事：決定要搜哪幾個資料庫、把每一個宣告成目標、比對、
/// 把命中換成 <see cref="SearchHit"/>。
///
/// 硬性規則寫在這裡，因為它們都是「少做一次就看不出來」的那種：
/// <see cref="SearchQuery.Targets"/> 在<b>取資料之前</b>問；分類過濾自己先套；
/// <see cref="ISearchSink.TryReport"/> 回 false 立刻停止；
/// <see cref="System.Data.Common.DbException"/> 一律降級成「這個目標讀不到」；
/// <b>每一個目標都要說出結局</b>——沒說的在收尾時記成已取消，畫面不會說「已完整搜尋」。
///
/// 多個資料庫<b>平行</b>掃，而且各自獨立：一個連不上、逾時或權限不足只會讓那一個目標讀不到，
/// 其他幾個照常回來。建索引的同時數量由索引快取限制，比對本身在記憶體裡。
/// </remarks>
public sealed class SqlCatalogSearchProvider : ISearchProvider
{
    /// <summary>跨版本穩定的識別字；分類 Id 與使用者偏好都以它為前綴。</summary>
    public const string ProviderId = "catalog";

    /// <summary>等索引建置時隔多久讀一次進度。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly ISqlConnectionSource _connectionSource;
    private readonly SqlSearchOrigin _origin;
    private readonly SqlCatalogSearchIndexCache _indexCache;
    private readonly Func<ISqlConnectionSource, CancellationToken, IReadOnlyList<SqlCatalogSearchDatabase>?> _listDatabases;
    private readonly Func<ISqlConnectionSource, SearchQuery, CancellationToken, SqlCatalogServerTextMatches?> _findOnServer;

    /// <param name="origin">
    /// <paramref name="connectionSource"/> 連著哪一台；每一筆命中都帶著它。由呼叫端給而不是
    /// 從連線推：連線來源只說得出快取鍵，而伺服器名稱的寫法只有接線層那一份。
    /// </param>
    /// <param name="indexCache">
    /// 索引快取；不給時自己建一份。同一個工具窗的多個 provider 實例要共用同一份時
    /// 由呼叫端傳進來——各自持有一份的症狀是同一個資料庫被掃好幾次全表。
    /// </param>
    /// <param name="listDatabases">
    /// 沒有指名資料庫（「全部」）時向伺服器要清單的那一條；測試換掉它就不必真的連資料庫。
    /// </param>
    /// <param name="findOnServer">
    /// 本文改在伺服器端比對的資料庫用的那一條；測試換掉它就不必真的連資料庫。
    /// </param>
    public SqlCatalogSearchProvider(
        ISqlConnectionSource connectionSource,
        SqlSearchOrigin origin,
        SqlCatalogSearchIndexCache? indexCache = null,
        Func<ISqlConnectionSource, CancellationToken, IReadOnlyList<SqlCatalogSearchDatabase>?>? listDatabases = null,
        Func<ISqlConnectionSource, SearchQuery, CancellationToken, SqlCatalogServerTextMatches?>? findOnServer = null)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _indexCache = indexCache ?? new SqlCatalogSearchIndexCache();
        _listDatabases = listDatabases ?? ((source, token) => SqlCatalogSearchDatabases.TryList(source, token));
        _findOnServer = findOnServer ?? ((source, query, token) => SqlCatalogServerTextSearch.TryFind(source, query, token));
        Categories = SqlCatalogSearchCategories.Create(ProviderId);
    }

    public string Id => ProviderId;

    public string DisplayName => SearchSourceText.CatalogDisplayName;

    public IReadOnlyList<SearchCategory> Categories { get; }

    /// <summary>這個 provider 用的索引快取；重新整理時整批丟掉用得到。</summary>
    public SqlCatalogSearchIndexCache IndexCache => _indexCache;

    public async Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (sink is null) throw new ArgumentNullException(nameof(sink));

        // 分類過濾把這個來源整個排除（例如只勾了作業）：不宣告目標、不建索引。
        // 靠聚合器那道最後防線的話，這一輪仍然要把每一個資料庫索引一遍，只為了讓結果被丟掉。
        if (!WantsAnyCategory(query)) return;

        var databases = await ResolveTargetsAsync(query.Scope, sink, cancellationToken).ConfigureAwait(false);
        var runs = new List<Task>(databases.Count);

        foreach (var (source, target) in databases)
        {
            runs.Add(SearchDatabaseAsync(source, target, query, sink, cancellationToken));
        }

        await Task.WhenAll(runs).ConfigureAwait(false);
    }

    /// <summary>
    /// 這一輪要搜哪幾個資料庫；每一個都先宣告成目標，讀不到的當場說出結局。
    /// </summary>
    /// <remarks>
    /// 沒有指名就是<b>全部</b>：這台伺服器上看得到的每一個（<see cref="SqlCatalogSearchDatabases.TryList"/>）。
    /// 進不去的（離線、還原中、沒有權限）也宣告成目標並說讀不到：直接略過的話，畫面會說
    /// 「已完整搜尋 38 個資料庫」，而使用者以為那是全部。清單問不到時照實說一句，<b>不</b>退回
    /// 只搜連線那一個——那一份答案看起來完全正常，只是少了使用者以為有搜的其他資料庫。
    ///
    /// 換資料庫換的是目錄，走 <see cref="SqlDatabaseScopedConnectionSource"/>：查詢一律寫成
    /// 不加限定的 <c>sys.</c>，決定查哪一個資料庫的是連線。換不過去時開連線會丟
    /// <see cref="System.Data.Common.DbException"/>，由索引那一層降級成「這個目標讀不到」——
    /// <b>絕不</b>退回拿目前連線裡同名的物件回答。
    ///
    /// 同一個名稱指名兩次只掃一次；指名了伺服器就整輪不回結果（沒有連結伺服器的索引）。
    /// </remarks>
    private async Task<IReadOnlyList<(ISqlConnectionSource Source, SearchTarget Target)>> ResolveTargetsAsync(
        SearchScope scope, ISearchSink sink, CancellationToken cancellationToken)
    {
        var resolved = new List<(ISqlConnectionSource, SearchTarget)>();

        if (scope.Servers.Count > 0) return resolved;

        IReadOnlyList<SqlCatalogSearchDatabase> databases;

        if (scope.Databases.Count == 0)
        {
            // 清單查詢是同步的阻塞工作；丟到背景，別讓同一輪的其他來源等它。
            var listed = await Task.Run(() => _listDatabases(_connectionSource, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            if (listed is null)
            {
                sink.AddTarget(DisplayName, SearchTargetKind.Source)
                    .Unavailable(detail: SearchSourceText.DatabaseListUnavailable);
                return resolved;
            }

            databases = listed;
        }
        else
        {
            var named = new List<SqlCatalogSearchDatabase>(scope.Databases.Count);
            foreach (var name in scope.Databases) if (name.Length != 0) named.Add(new SqlCatalogSearchDatabase(name, isSystem: false));
            databases = named;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in databases)
        {
            if (!seen.Add(database.Name)) continue;

            var target = sink.AddTarget(database.Name, SearchTargetKind.Database);

            if (!database.IsAccessible)
            {
                // 狀態是伺服器說的原樣（OFFLINE、RESTORING），不翻譯；在線上卻進不去就是權限。
                target.Unavailable(
                    database.IsDenied ? SearchUnavailableKind.Denied : SearchUnavailableKind.Unknown,
                    database.IsDenied ? null : database.State);
                continue;
            }

            resolved.Add((
                string.Equals(database.Name, _connectionSource.DatabaseName, StringComparison.OrdinalIgnoreCase)
                    ? _connectionSource
                    : new SqlDatabaseScopedConnectionSource(_connectionSource, database.Name),
                target));
        }

        return resolved;
    }

    /// <summary>掃一個資料庫；結局一律寫在 <paramref name="target"/> 上。</summary>
    /// <remarks>
    /// 等索引時只是<b>等</b>：建置是索引快取自己的工作，這一輪被取消（使用者又打了一個字）
    /// 不會把建到一半的索引丟掉，下一輪接著等同一份。
    /// </remarks>
    private async Task SearchDatabaseAsync(
        ISqlConnectionSource source,
        SearchTarget target,
        SearchQuery query,
        ISearchSink sink,
        CancellationToken cancellationToken)
    {
        // 空輸入是「列一份預設清單」，不是「把整個資料庫倒出來」，所以連本文那一段的
        // 索引都不必建——本文比對對空樣式沒有意義（每一個位置都命中）。
        var needsText = !query.IsEmpty && query.IncludesTarget(SearchMatchTarget.Text);
        var build = _indexCache.Build(source, needsText);

        while (!build.Task.IsCompleted)
        {
            target.ReportProgress(build.Progress);
            await Task.WhenAny(build.Task, Task.Delay(ProgressInterval, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var result = await build.Task.ConfigureAwait(false);

        if (result.Index is not { } index)
        {
            // 這一輪沒有這個資料庫的資料。其他資料庫照掃——一個連不上的目標
            // 讓整份結果消失，比少一個來源糟得多。
            target.Unavailable(result.UnavailableKind);
            return;
        }

        target.ReportProgress(1);

        // 本文不在記憶體裡的資料庫先到伺服器端比對；與建索引共用名額，幾十個這種資料庫不會
        // 在同一刻各送一條全表掃描。失敗是 null，由 Scan 說本文沒比完。
        var onServer = needsText && index.TextOnServer
            ? await _indexCache.RunLimitedAsync(() => _findOnServer(source, query, cancellationToken), cancellationToken)
                .ConfigureAwait(false)
            : null;

        // 比對是 CPU 工作；丟到背景，幾個資料庫才真的平行。
        await Task.Run(() => Scan(index, onServer, target, query, sink, needsText, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <remarks>
    /// 名稱命中先掃完再掃資料行與本文：名稱命中在使用者還在打字時就要上畫面，
    /// 而本文要把整份定義掃過。
    /// </remarks>
    private void Scan(
        SqlCatalogSearchIndex index,
        SqlCatalogServerTextMatches? onServer,
        SearchTarget target,
        SearchQuery query,
        ISearchSink sink,
        bool needsText,
        CancellationToken cancellationToken)
    {
        var badges = new[] { new SearchBadge(index.DatabaseName, SearchBadge.DatabaseIcon) };

        if (query.IncludesTarget(SearchMatchTarget.Name) &&
            !SearchObjectNames(index, _origin, query, sink, badges, cancellationToken))
        {
            return;
        }

        if (!query.IsEmpty && query.IncludesTarget(SearchMatchTarget.Column) &&
            !SearchColumnNames(index, _origin, query, sink, badges, cancellationToken))
        {
            return;
        }

        if (needsText && !SearchText(index, onServer, target, query, sink, badges, cancellationToken))
        {
            return;
        }

        target.Complete();
    }

    private static bool SearchObjectNames(
        SqlCatalogSearchIndex index,
        SqlSearchOrigin origin,
        SearchQuery query,
        ISearchSink sink,
        IReadOnlyList<SearchBadge> badges,
        CancellationToken cancellationToken)
    {
        foreach (var info in index.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var categoryId = SqlCatalogSearchCategories.IdFor(info.Kind);

            if (categoryId is null || !query.MatchesCategory(categoryId))
            {
                continue;
            }

            // 一個修飾都沒開才走模糊比對；開了大小寫或全字就是字面比對，規則與本文那一段
            // 同一份，見 SearchIdentifierMatch。
            var match = SearchIdentifierMatch.Match(query, info.Name);

            if (!match.IsMatch)
            {
                continue;
            }

            var hit = new SearchHit(
                ProviderId,
                categoryId,
                SearchMatchTarget.Name,
                info.QualifiedName,
                DedupeKeyFor(info),
                match.Score,
                PathFor(info),
                // 名稱命中的片段就是名稱本體：高亮區段的索引落在片段上，
                // 沒有這一份就沒有地方放那些區段。
                info.Name,
                match.Spans,
                new SqlCatalogSearchTarget(
                    origin, index.DatabaseName, info.SchemaName, info.Name, info.Kind, info.ObjectId),
                badges);

            if (!sink.TryReport(hit))
            {
                return false;
            }
        }

        return true;
    }

    /// <remarks>
    /// 資料行命中掛在<b>它所屬物件</b>的分類上，不是自己一種分類：一個資料行講的是
    /// 「這張資料表上有一行叫這個名字」，使用者勾「只看資料表」時要的正是它。
    /// 它與物件命中的差別在 <see cref="SearchMatchTarget.Column"/>，那是另一條軸。
    /// </remarks>
    private static bool SearchColumnNames(
        SqlCatalogSearchIndex index,
        SqlSearchOrigin origin,
        SearchQuery query,
        ISearchSink sink,
        IReadOnlyList<SearchBadge> badges,
        CancellationToken cancellationToken)
    {
        foreach (var column in index.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var owner = column.Owner;
            var categoryId = SqlCatalogSearchCategories.IdFor(owner.Kind);

            if (categoryId is null || !query.MatchesCategory(categoryId))
            {
                continue;
            }

            var match = SearchIdentifierMatch.Match(query, column.Name);

            if (!match.IsMatch)
            {
                continue;
            }

            var hit = new SearchHit(
                ProviderId,
                categoryId,
                SearchMatchTarget.Column,
                // 標題是<b>物件</b>的限定名稱，不接資料行那一段：聚合器會把同一張表的幾個
                // 資料行命中併成一列，而那一列的抬頭不該是其中隨便一行的名字。命中的是哪幾行
                // 由片段（資料行名稱）回答，呈現那一層把它們列在第二列上。
                owner.QualifiedName,
                DedupeKeyFor(owner),
                match.Score,
                // 路徑指向<b>物件</b>，不是資料行：四段式名稱的第一段是連結伺服器，
                // 把資料行接成第四段會讓下游把資料庫名讀成伺服器名。
                PathFor(owner),
                column.Name,
                match.Spans,
                new SqlCatalogSearchTarget(
                    origin, index.DatabaseName, owner.SchemaName, owner.Name, owner.Kind, owner.ObjectId, column.Name),
                badges);

            if (!sink.TryReport(hit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 比對定義本文：記憶體裡有就在記憶體裡比，沒有就到伺服器端比。
    /// </summary>
    /// <remarks>
    /// 兩條路交出同一種命中、同一種「讀不到」的計數，差別只在本文從哪裡來。讀不到的本文
    /// （加密、沒有 VIEW DEFINITION）只數這一輪分類過濾留下的那幾種：使用者只勾資料表時，
    /// 一個加密的預存程序與他的問題無關。
    /// </remarks>
    /// <returns>false 表示這一輪已經被取代，呼叫端立刻停止。</returns>
    /// <param name="onServer">本文在伺服器端比對時的結果；比對失敗時為 null。</param>
    private bool SearchText(
        SqlCatalogSearchIndex index,
        SqlCatalogServerTextMatches? onServer,
        SearchTarget target,
        SearchQuery query,
        ISearchSink sink,
        IReadOnlyList<SearchBadge> badges,
        CancellationToken cancellationToken)
    {
        var unreadable = 0;

        if (index.Definitions is { } definitions)
        {
            foreach (var info in index.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Wanted(query, info, out var categoryId)) continue;

                if (definitions.For(info.ObjectId) is not { } definition)
                {
                    if (definitions.IsUnreadable(info.ObjectId)) unreadable++;
                    continue;
                }

                if (!ReportText(_origin, index, info, categoryId, definition, query, sink, badges)) return false;
            }
        }
        else if (index.TextOnServer)
        {
            if (onServer is not { } found)
            {
                // 伺服器端比對失敗：名稱與資料行照樣算數，但本文這一段沒比到。
                target.MarkTextIncomplete();
                return true;
            }

            foreach (var objectId in found.Unreadable)
            {
                if (index.Find(objectId) is { } info && Wanted(query, info, out _)) unreadable++;
            }

            foreach (var match in found.Matches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (index.Find(match.Key) is not { } info || !Wanted(query, info, out var categoryId)) continue;
                if (!ReportText(_origin, index, info, categoryId, match.Value, query, sink, badges)) return false;
            }
        }

        target.AddUnreadableText(unreadable);
        return true;
    }

    private static bool Wanted(SearchQuery query, SqlObjectInfo info, out string categoryId)
    {
        categoryId = SqlCatalogSearchCategories.IdFor(info.Kind) ?? "";
        return categoryId.Length != 0 && query.MatchesCategory(categoryId);
    }

    /// <returns>false 表示這一輪已經被取代。</returns>
    private static bool ReportText(
        SqlSearchOrigin origin,
        SqlCatalogSearchIndex index,
        SqlObjectInfo info,
        string categoryId,
        string definition,
        SearchQuery query,
        ISearchSink sink,
        IReadOnlyList<SearchBadge> badges)
    {
        // 本文比的是使用者打進去的原文，不是正規化後的樣式：後者一律小寫，
        // 拿它做區分大小寫的比對永遠比不中任何大寫的字。
        var matches = SqlCatalogBodySearch.FindAll(definition, query.Matcher);

        if (matches.Count == 0)
        {
            return true;
        }

        var snippet = SqlCatalogBodySearch.BuildSnippet(definition, matches, query.Text.Length, out var spans);

        return sink.TryReport(new SearchHit(
            ProviderId,
            categoryId,
            SearchMatchTarget.Text,
            info.QualifiedName,
            DedupeKeyFor(info),
            // 本文的分數是「提到幾次」。與名稱那一組不同尺度沒有關係：
            // SearchMatchTarget 已經把兩組分開排，兩邊的分數不會互相比較。
            matches.Count,
            PathFor(info),
            snippet,
            spans,
            new SqlCatalogSearchTarget(
                origin, index.DatabaseName, info.SchemaName, info.Name, info.Kind, info.ObjectId),
            badges));
    }

    private bool WantsAnyCategory(SearchQuery query)
    {
        foreach (var category in Categories) if (query.MatchesCategory(category.Id)) return true;
        return false;
    }

    /// <summary>
    /// 跨 provider 穩定的去重鍵：資料庫、結構描述、名稱，資料行再加一段。
    /// </summary>
    /// <remarks>
    /// 資料庫名稱非有不可：兩個資料庫裡各有一張 <c>Loan</c> 是常態，少了這一段
    /// 其中一列會被去重吃掉，而使用者看不出少了哪一個。
    ///
    /// 不含命中部位：同一個物件被名稱與定義本文同時命中時，聚合器要把它們併成一列，
    /// 靠的就是兩邊寫出同一個鍵。<b>資料行命中也走物件這一份鍵</b>——一張表有三個資料行對上時，
    /// 使用者要的是一列 <c>Cat_BookCopy</c> 加上「命中了這三行」，不是三列同一張表。
    ///
    /// 刻意不轉小寫：定序可以是區分大小寫的，那時 <c>Loan</c> 與 <c>LOAN</c> 是兩個
    /// 不同的資料表，折成同一個鍵會讓其中一個永遠不出現。
    /// </remarks>
    private static string DedupeKeyFor(SqlObjectInfo info)
    {
        var database = info.DatabaseName is { Length: > 0 } name
            ? SqlIdentifier.Quote(name) + "."
            : string.Empty;

        return database + info.QualifiedName;
    }

    /// <summary>
    /// 命中的限定名稱，帶著資料庫名稱。
    /// </summary>
    /// <remarks>
    /// <c>object_id</c> 只在自己那個資料庫裡唯一，所以下游不得拿它跨庫查——而它唯一
    /// 分得出「這是哪一個資料庫的」的線索就是這條路徑與
    /// <see cref="SqlCatalogSearchTarget.DatabaseName"/>。
    ///
    /// 結構描述那一段即使是空的也照放：<see cref="SqlObjectPath"/> 是右對齊的，
    /// 少放一段會讓資料庫名稱掉進結構描述那一格，而那個「結構描述」並不存在。
    /// </remarks>
    private static SqlObjectPath? PathFor(SqlObjectInfo info)
    {
        var parts = info.DatabaseName is { Length: > 0 } database
            ? new[] { database, info.SchemaName, info.Name }
            : info.SchemaName.Length > 0
                ? new[] { info.SchemaName, info.Name }
                : new[] { info.Name };

        return SqlObjectPath.TryParseName(parts, out var path) ? path : null;
    }
}
