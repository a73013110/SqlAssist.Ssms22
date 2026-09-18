using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Matching;
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
/// 分開見那個型別。這一層只做三件事：決定要搜哪幾個資料庫、比對、把命中換成
/// <see cref="SearchHit"/>。
///
/// 三條硬性規則寫在這裡，因為它們都是「少做一次就看不出來」的那種：
/// 分類過濾自己先套（靠聚合器那道最後防線，會把使用者已經勾掉的候選算進預算）；
/// <see cref="ISearchSink.TryReport"/> 回 false 立刻停止（繼續掃的結果全部會被丟掉，
/// 而那一輪的延遲仍然要使用者等）；<see cref="System.Data.Common.DbException"/> 一律降級
/// （冒出去會在平台邊界留下每按一次鍵一份的完整堆疊）。
/// </remarks>
public sealed class SqlCatalogSearchProvider : ISearchProvider
{
    /// <summary>跨版本穩定的識別字；分類 Id 與使用者偏好都以它為前綴。</summary>
    public const string ProviderId = "catalog";

    /// <summary>每檢查幾個候選回報一次。</summary>
    /// <remarks>
    /// 逐筆呼叫太吵——一個資料庫的候選是以萬計的，而 sink 每一次都要做一次預算判斷。
    /// 攢一批再報，代價是最多超掃這麼多個候選才發現預算用盡。
    /// </remarks>
    private const int ExamineBatch = 64;

    private readonly ISqlConnectionSource _connectionSource;
    private readonly SqlCatalogSearchIndexCache _indexCache;

    /// <param name="indexCache">
    /// 索引快取；不給時自己建一份。同一個查詢視窗的多個 provider 實例要共用同一份時
    /// 由呼叫端傳進來——各自持有一份的症狀是同一個資料庫被掃好幾次全表。
    /// </param>
    public SqlCatalogSearchProvider(
        ISqlConnectionSource connectionSource,
        SqlCatalogSearchIndexCache? indexCache = null)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
        _indexCache = indexCache ?? new SqlCatalogSearchIndexCache();
        Categories = SqlCatalogSearchCategories.Create(ProviderId);
    }

    public string Id => ProviderId;

    public string DisplayName => "資料庫物件";

    public IReadOnlyList<SearchCategory> Categories { get; }

    /// <summary>這個 provider 用的索引快取；重新整理時整批丟掉用得到。</summary>
    public SqlCatalogSearchIndexCache IndexCache => _indexCache;

    /// <remarks>
    /// 走 <see cref="Task.Run(Action, CancellationToken)"/>：索引建立與掃描都是同步的
    /// 阻塞工作，而聚合器是直接 await 每一個 provider 的——在呼叫端的執行緒上跑完的話，
    /// 幾個來源會變成一個接一個，而搜尋面板要的正是名稱命中先上畫面。
    /// 這與 <c>SqlMetadataCatalog</c> 把載入丟進 <c>Task.Run</c> 是同一個作法。
    /// </remarks>
    public Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (sink is null) throw new ArgumentNullException(nameof(sink));

        return Task.Run(() => Search(query, sink, cancellationToken), cancellationToken);
    }

    private void Search(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
    {
        var counter = new ExamineCounter(sink);
        var truncated = false;

        foreach (var source in ResolveSources(query.Scope))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 下一個資料庫的索引可能要掃一次全表；預算已經滿了就別付這個代價。
            if (sink.IsExhausted)
            {
                truncated = true;
                break;
            }

            var index = _indexCache.GetOrBuild(source, cancellationToken);

            if (index is null)
            {
                // 這一輪沒有這個資料庫的資料。其他資料庫照掃——一個連不上的目標
                // 讓整份結果消失，比少一個來源糟得多。
                truncated = true;
                continue;
            }

            // 本文只收到一半也是「沒掃完」。不說的話，使用者看到的與「這個字串
            // 在這個資料庫裡不存在」一模一樣。
            if (!query.IsEmpty && !index.HasCompleteDefinitions)
            {
                truncated = true;
            }

            if (!SearchIndex(index, query, sink, counter, cancellationToken))
            {
                truncated = true;
                break;
            }
        }

        counter.Flush();

        if (truncated)
        {
            sink.ReportTruncated(counter.Checkpoint);
        }
    }

    /// <summary>掃一個資料庫；回傳 false 表示收到停止訊號，整輪收工。</summary>
    /// <remarks>
    /// 名稱命中先掃完再掃本文，不是一個物件同時算兩種：名稱命中在使用者還在打字時
    /// 就要上畫面，而本文命中要把定義本文整份掃過。混在一起的話，預算會被前幾個
    /// 物件的本文吃掉，而後面那些名稱一模一樣的物件連比都沒比到。
    /// </remarks>
    private static bool SearchIndex(
        SqlCatalogSearchIndex index,
        SearchQuery query,
        ISearchSink sink,
        ExamineCounter counter,
        CancellationToken cancellationToken)
    {
        if (!SearchObjectNames(index, query, sink, counter, cancellationToken))
        {
            return false;
        }

        // 空輸入是「列一份預設清單」，不是「把整個資料庫倒出來」：資料行的數量是
        // 物件的幾十倍，而本文比對對空樣式沒有意義（每一個位置都命中）。
        if (query.IsEmpty)
        {
            return true;
        }

        return SearchColumnNames(index, query, sink, counter, cancellationToken) &&
               SearchDefinitions(index, query, sink, counter, cancellationToken);
    }

    private static bool SearchObjectNames(
        SqlCatalogSearchIndex index,
        SearchQuery query,
        ISearchSink sink,
        ExamineCounter counter,
        CancellationToken cancellationToken)
    {
        foreach (var entry in index.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var categoryId = SqlCatalogSearchCategories.IdFor(entry.Info.Kind);

            // 過濾掉的候選連算都不算：算進去的話，勾掉九成分類的那一輪仍然要付
            // 十成的預算，而預算用盡時被砍掉的是使用者真的要的那一成。
            if (categoryId is null || !query.MatchesCategory(categoryId))
            {
                continue;
            }

            var info = entry.Info;
            var key = DedupeKeyFor(info, null);
            counter.Note(key);

            // 名稱走模糊比對，大小寫一律不分：FuzzyMatcher 是為識別字設計的，
            // 而 MatchCasing 只作用在本文那一段——v1 刻意如此，識別字在多數定序下
            // 本來就不分大小寫，逐字比對會讓 PUBLISHER 打成 publisher 就一筆都不剩。
            var match = FuzzyMatcher.MatchNormalized(query.NormalizedPattern, info.Name);

            if (!match.IsMatch)
            {
                continue;
            }

            var hit = new SearchHit(
                ProviderId,
                categoryId,
                SearchHitClass.Name,
                info.QualifiedName,
                key,
                match.Score,
                PathFor(info),
                // 名稱命中的片段就是名稱本體：高亮區段的索引落在片段上，
                // 沒有這一份就沒有地方放那些區段。
                info.Name,
                match.Spans,
                new SqlCatalogSearchTarget(
                    index.DatabaseName, info.SchemaName, info.Name, info.Kind, info.ObjectId));

            if (!sink.TryReport(hit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SearchColumnNames(
        SqlCatalogSearchIndex index,
        SearchQuery query,
        ISearchSink sink,
        ExamineCounter counter,
        CancellationToken cancellationToken)
    {
        if (!query.MatchesCategory(SqlCatalogSearchCategories.ColumnCategoryId))
        {
            return true;
        }

        foreach (var column in index.Columns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var owner = column.Owner;
            var key = DedupeKeyFor(owner, column.Name);
            counter.Note(key);

            var match = FuzzyMatcher.MatchNormalized(query.NormalizedPattern, column.Name);

            if (!match.IsMatch)
            {
                continue;
            }

            var hit = new SearchHit(
                ProviderId,
                SqlCatalogSearchCategories.ColumnCategoryId,
                SearchHitClass.Name,
                owner.QualifiedName + "." + SqlIdentifier.Quote(column.Name),
                key,
                match.Score,
                // 路徑指向<b>物件</b>，不是資料行：四段式名稱的第一段是連結伺服器，
                // 把資料行接成第四段會讓下游把資料庫名讀成伺服器名。
                PathFor(owner),
                column.Name,
                match.Spans,
                new SqlCatalogSearchTarget(
                    index.DatabaseName, owner.SchemaName, owner.Name, owner.Kind, owner.ObjectId, column.Name));

            if (!sink.TryReport(hit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SearchDefinitions(
        SqlCatalogSearchIndex index,
        SearchQuery query,
        ISearchSink sink,
        ExamineCounter counter,
        CancellationToken cancellationToken)
    {
        foreach (var entry in index.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Definition is not { } definition)
            {
                continue;
            }

            var categoryId = SqlCatalogSearchCategories.IdFor(entry.Info.Kind);

            if (categoryId is null || !query.MatchesCategory(categoryId))
            {
                continue;
            }

            var info = entry.Info;
            var key = DedupeKeyFor(info, null);
            counter.Note(key);

            // 本文比的是使用者打進去的原文，不是正規化後的樣式：後者一律小寫，
            // 拿它做區分大小寫的比對永遠比不中任何大寫的字。
            var matches = SqlCatalogBodySearch.FindAll(definition, query.Text, query.Options);

            if (matches.Count == 0)
            {
                continue;
            }

            var snippet = SqlCatalogBodySearch.BuildSnippet(definition, matches, query.Text.Length, out var spans);

            var hit = new SearchHit(
                ProviderId,
                categoryId,
                SearchHitClass.Body,
                info.QualifiedName,
                key,
                // 本文的分數是「提到幾次」。與名稱那一組不同尺度沒有關係：
                // SearchHitClass 已經把兩組分開排，兩邊的分數不會互相比較。
                matches.Count,
                PathFor(info),
                snippet,
                spans,
                new SqlCatalogSearchTarget(
                    index.DatabaseName, info.SchemaName, info.Name, info.Kind, info.ObjectId));

            if (!sink.TryReport(hit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 這一輪要搜哪幾個資料庫。
    /// </summary>
    /// <remarks>
    /// 沒有指名就只搜目前這條連線的那一個——把每一個進得去的資料庫都索引一遍
    /// 是明文禁止的：共用主機上等於幾十次全表掃描，而其中九成九不會有人搜。
    ///
    /// 指名了就換目錄，走 <see cref="SqlDatabaseScopedConnectionSource"/>：查詢一律寫成
    /// 不加限定的 <c>sys.</c>，決定查哪一個資料庫的是連線。換不過去（資料庫不存在、
    /// 離線、沒有權限）時開連線會丟 <see cref="System.Data.Common.DbException"/>，
    /// 由索引那一層降級成「這一輪沒有這個資料庫的資料」——<b>絕不</b>退回拿目前連線裡
    /// 同名的物件回答，那比什麼都不做糟，什麼都不做至少是沉默。
    ///
    /// 指名了伺服器就整輪不回結果：v1 沒有連結伺服器（四段式名稱）的索引，
    /// 而拿本機的東西當成對面那台的答案正是上一段禁止的事。連結伺服器要的是
    /// <c>SqlCatalogQualifier</c> 與 <c>OPENQUERY</c> 那一條路，不是換個資料庫就行。
    /// </remarks>
    private IEnumerable<ISqlConnectionSource> ResolveSources(SearchScope scope)
    {
        if (scope.Servers.Count > 0)
        {
            yield break;
        }

        if (scope.Databases.Count == 0)
        {
            yield return _connectionSource;
            yield break;
        }

        foreach (var databaseName in scope.Databases)
        {
            if (databaseName.Length == 0)
            {
                continue;
            }

            yield return string.Equals(
                databaseName, _connectionSource.DatabaseName, StringComparison.OrdinalIgnoreCase)
                ? _connectionSource
                : new SqlDatabaseScopedConnectionSource(_connectionSource, databaseName);
        }
    }

    /// <summary>
    /// 跨 provider 穩定的去重鍵：資料庫、結構描述、名稱，資料行再加一段。
    /// </summary>
    /// <remarks>
    /// 資料庫名稱非有不可：兩個資料庫裡各有一張 <c>Loan</c> 是常態，少了這一段
    /// 其中一列會被去重吃掉，而使用者看不出少了哪一個。
    ///
    /// 刻意不轉小寫：定序可以是區分大小寫的，那時 <c>Loan</c> 與 <c>LOAN</c> 是兩個
    /// 不同的資料表，折成同一個鍵會讓其中一個永遠不出現。
    /// </remarks>
    private static string DedupeKeyFor(SqlObjectInfo info, string? columnName)
    {
        var database = info.DatabaseName is { Length: > 0 } name
            ? SqlIdentifier.Quote(name) + "."
            : string.Empty;

        return columnName is null
            ? database + info.QualifiedName
            : database + info.QualifiedName + "." + SqlIdentifier.Quote(columnName);
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

    /// <summary>
    /// 攢一批候選再回報一次，並記住掃到哪裡。
    /// </summary>
    /// <remarks>
    /// 續掃位置是不透明字串，Core 不解讀也不會自動續搜——沿用 SQL Memory 搜尋的作法，
    /// 是否往前找由呼叫端決定。
    /// </remarks>
    private sealed class ExamineCounter
    {
        private readonly ISearchSink _sink;
        private int _pending;

        internal ExamineCounter(ISearchSink sink) => _sink = sink;

        /// <summary>最後一個檢查過的候選；截斷時當續掃位置交出去。</summary>
        internal string? Checkpoint { get; private set; }

        internal void Note(string candidateKey)
        {
            Checkpoint = candidateKey;

            if (++_pending >= ExamineBatch)
            {
                Flush();
            }
        }

        internal void Flush()
        {
            if (_pending == 0)
            {
                return;
            }

            _sink.ReportExamined(_pending);
            _pending = 0;
        }
    }
}
