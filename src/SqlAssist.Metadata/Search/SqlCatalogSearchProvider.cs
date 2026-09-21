using System;
using System.Collections.Generic;
using System.Globalization;
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
/// 分開見那個型別。這一層只做三件事：決定要搜哪幾個資料庫、比對、把命中換成
/// <see cref="SearchHit"/>。
///
/// 四條硬性規則寫在這裡，因為它們都是「少做一次就看不出來」的那種：
/// <see cref="SearchQuery.Targets"/> 在<b>取資料之前</b>問（掃回來再丟的話，
/// 第一次搜尋最貴的那一段一毫秒都沒省到）；分類過濾自己先套（靠聚合器那道最後防線，
/// 會把使用者已經勾掉的候選算進預算）；<see cref="ISearchSink.TryReport"/> 回 false 立刻停止
/// （繼續掃的結果全部會被丟掉，而那一輪的延遲仍然要使用者等）；
/// <see cref="System.Data.Common.DbException"/> 一律降級（冒出去會在平台邊界留下
/// 每按一次鍵一份的完整堆疊）。
///
/// 多個資料庫<b>平行</b>掃，而且各自獨立：一個連不上、逾時或權限不足只會讓那一個沒有結果
/// 並走 <see cref="ISearchSink.ReportUnavailable(string)"/> 說出是哪一個，其他幾個照常回來
/// （<b>不是</b>「沒掃完」——那一句叫使用者縮小範圍，而那對一個連不上的資料庫一次都幫不上
/// 忙）。排成一列掃的症狀是使用者勾了五個資料庫之後，
/// 第五個要等前四個都掃完全表才開始，而它們用的是各自的連線。
/// </remarks>
public sealed class SqlCatalogSearchProvider : ISearchProvider
{
    /// <summary>跨版本穩定的識別字；分類 Id 與使用者偏好都以它為前綴。</summary>
    public const string ProviderId = "catalog";

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
    /// 每一個資料庫各走一次 <see cref="Task.Run(Action, CancellationToken)"/>：索引建立與掃描
    /// 都是同步的阻塞工作，而聚合器是直接 await 每一個 provider 的——在呼叫端的執行緒上跑完
    /// 的話，幾個來源會變成一個接一個，而搜尋面板要的正是名稱命中先上畫面。
    /// 這與 <c>SqlMetadataCatalog</c> 把載入丟進 <c>Task.Run</c> 是同一個作法。
    ///
    /// sink 依契約可以被多執行緒呼叫，所以幾個資料庫可以同時往裡面推；最後的排序由聚合器
    /// 負責，回來的先後不影響清單的順序。
    /// </remarks>
    public async Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (sink is null) throw new ArgumentNullException(nameof(sink));

        var sources = ResolveSources(query.Scope);

        if (sources.Count == 0)
        {
            return;
        }

        var rounds = new DatabaseRound[sources.Count];
        var runs = new Task[sources.Count];

        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var round = new DatabaseRound(source.DatabaseName);
            rounds[index] = round;
            runs[index] = Task.Run(
                () => SearchDatabase(source, query, sink, round, _indexCache, cancellationToken), cancellationToken);
        }

        try
        {
            await Task.WhenAll(runs).ConfigureAwait(false);
        }
        finally
        {
            Summarize(rounds, sink);
        }
    }

    /// <summary>
    /// 把幾個資料庫各自的結果收成這一輪的一句話。
    /// </summary>
    /// <remarks>
    /// 續掃位置取<b>第一個沒掃完的資料庫</b>的，不是最後一個回來的：後者由賽跑決定，
    /// 同一組輸入每次交出去的字串會不一樣，而呼叫端可能拿它決定要不要往下找。
    /// 讀不到的資料庫名稱同理照 <paramref name="rounds"/> 的順序（＝使用者勾的順序）收，
    /// 不照誰先回來。
    ///
    /// 「讀不到」與「沒掃完」分開回報，而且可以同時發生：五個資料庫裡一個連不上、
    /// 另一個掃到預算用盡是一輪裡的兩件事，而它們要說的話不一樣。
    /// </remarks>
    private static void Summarize(DatabaseRound[] rounds, ISearchSink sink)
    {
        var truncated = false;
        string? checkpoint = null;
        var unavailable = new List<string>();
        var allDenied = true;

        foreach (var round in rounds)
        {
            if (round.Unavailable)
            {
                unavailable.Add(round.DatabaseName);

                // 五個資料庫裡一個沒權限、另一個連不上：整組退回「說不出是哪一種」。
                // 只要有一個不是權限問題，「權限不足」就是錯的斷言，而使用者會去查一個
                // 好好的權限設定。
                allDenied &= round.UnavailableKind == SearchUnavailableKind.Denied;
            }

            if (!round.Truncated) continue;

            truncated = true;
            checkpoint ??= round.Checkpoint;
        }

        if (truncated) sink.ReportTruncated(checkpoint);

        if (unavailable.Count > 0)
        {
            var kind = allDenied ? SearchUnavailableKind.Denied : SearchUnavailableKind.Unknown;
            sink.ReportUnavailable(UnavailableReason(unavailable, kind), kind);
        }
    }

    /// <summary>
    /// 讀不到的資料庫交給呼叫端貼在狀態列上的那一句話。
    /// </summary>
    /// <remarks>
    /// 名稱一定要寫出來。泛用的「部分結果；縮小範圍或加長關鍵字可以掃得更完整」對
    /// 「LibArchive 連不上」完全沒有用——使用者會照那一句改三次關鍵字，而那個資料庫
    /// 一次都沒有被搜到。
    ///
    /// 不逐一列名，只寫第一個加上還有幾個：狀態列是一行，而勾了十個資料庫、斷了八個的
    /// 那一輪會把它撐爆，重點（有東西沒搜到、去看那幾個資料庫）第一句已經說完。
    ///
    /// 括號裡寫的是幾個可能還是一句斷言，由
    /// <see cref="SqlCatalogSearchIndexCache.GetOrBuild"/> 交出來的
    /// <see cref="SearchUnavailableKind"/> 決定。伺服器給了權限錯誤碼才斷言權限；
    /// 說不出來時照舊列出幾個可能——斷言錯的那一次會讓使用者去查一個好好的權限設定，
    /// 而他怎麼查都查不出問題。
    /// </remarks>
    private static string UnavailableReason(IReadOnlyList<string> databaseNames, SearchUnavailableKind kind)
    {
        var first = databaseNames[0];

        var subject = databaseNames.Count > 1
            ? "「" + first + "」等 " + databaseNames.Count.ToString(CultureInfo.InvariantCulture) + " 個資料庫"
            : first.Length > 0 ? "「" + first + "」" : "有一個資料庫";

        return kind == SearchUnavailableKind.Denied
            ? subject + "這一輪讀不到（這個登入對它沒有權限），這一輪少了它的結果。"
            : subject + "這一輪讀不到（連不上、逾時，或這個登入對它沒有權限），這一輪少了它的結果。";
    }

    /// <summary>掃一個資料庫；失敗與截斷都只記在自己那一份 <paramref name="round"/> 上。</summary>
    /// <remarks>
    /// 名稱命中先掃完再掃資料行與本文，不是一個物件同時算三種：名稱命中在使用者還在打字時
    /// 就要上畫面，而本文命中要把定義本文整份掃過。混在一起的話，預算會被前幾個
    /// 物件的本文吃掉，而後面那些名稱一模一樣的物件連比都沒比到。
    /// </remarks>
    private static void SearchDatabase(
        ISqlConnectionSource source,
        SearchQuery query,
        ISearchSink sink,
        DatabaseRound round,
        SqlCatalogSearchIndexCache indexCache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 每個資料庫自己一份計數器：共用一份要為它上一把鎖，而那把鎖會落在最熱的迴圈裡。
        var counter = new SearchExamineCounter(sink);

        try
        {
            // 這個資料庫的索引可能要掃一次全表；預算已經滿了就別付這個代價。
            if (sink.IsExhausted)
            {
                round.Truncated = true;
                return;
            }

            // 空輸入是「列一份預設清單」，不是「把整個資料庫倒出來」，所以連本文那一段的
            // 索引都不必建——本文比對對空樣式沒有意義（每一個位置都命中）。
            var needsText = !query.IsEmpty && query.IncludesTarget(SearchMatchTarget.Text);
            var index = indexCache.GetOrBuild(source, needsText, cancellationToken, out var unavailableKind);

            if (index is null)
            {
                // 這一輪沒有這個資料庫的資料。其他資料庫照掃——一個連不上的目標
                // 讓整份結果消失，比少一個來源糟得多。
                //
                // 這不是「沒掃完」：一個字都沒掃到，而叫使用者縮小範圍或加長關鍵字
                // 對一個連不上的資料庫一次都幫不上忙。名稱由 Summarize 寫進那一句話裡。
                round.Unavailable = true;
                round.UnavailableKind = unavailableKind;
                return;
            }

            // 本文只收到一半也是「沒掃完」。不說的話，使用者看到的與「這個字串
            // 在這個資料庫裡不存在」一模一樣。
            if (needsText && index.Definitions is { IsComplete: false })
            {
                round.Truncated = true;
            }

            var badges = new[] { new SearchBadge(index.DatabaseName, SearchBadge.DatabaseIcon) };

            if (query.IncludesTarget(SearchMatchTarget.Name) &&
                !SearchObjectNames(index, query, sink, counter, badges, cancellationToken))
            {
                round.Truncated = true;
                return;
            }

            if (query.IsEmpty)
            {
                return;
            }

            if (query.IncludesTarget(SearchMatchTarget.Column) &&
                !SearchColumnNames(index, query, sink, counter, badges, cancellationToken))
            {
                round.Truncated = true;
                return;
            }

            if (needsText && !SearchDefinitions(index, query, sink, counter, badges, cancellationToken))
            {
                round.Truncated = true;
            }
        }
        finally
        {
            round.Checkpoint = counter.Checkpoint;
            counter.Flush();
        }
    }

    private static bool SearchObjectNames(
        SqlCatalogSearchIndex index,
        SearchQuery query,
        ISearchSink sink,
        SearchExamineCounter counter,
        IReadOnlyList<SearchBadge> badges,
        CancellationToken cancellationToken)
    {
        foreach (var info in index.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var categoryId = SqlCatalogSearchCategories.IdFor(info.Kind);

            // 過濾掉的候選連算都不算：算進去的話，勾掉九成分類的那一輪仍然要付
            // 十成的預算，而預算用盡時被砍掉的是使用者真的要的那一成。
            if (categoryId is null || !query.MatchesCategory(categoryId))
            {
                continue;
            }

            var key = DedupeKeyFor(info, null);
            counter.Note(key);

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
                key,
                match.Score,
                PathFor(info),
                // 名稱命中的片段就是名稱本體：高亮區段的索引落在片段上，
                // 沒有這一份就沒有地方放那些區段。
                info.Name,
                match.Spans,
                new SqlCatalogSearchTarget(
                    index.DatabaseName, info.SchemaName, info.Name, info.Kind, info.ObjectId),
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
        SearchQuery query,
        ISearchSink sink,
        SearchExamineCounter counter,
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

            // 續掃位置要認得是哪一行，去重鍵不能：前者是「掃到哪裡」，後者是「這是哪一個東西」，
            // 而一張表的三個資料行命中講的是同一張表。
            counter.Note(DedupeKeyFor(owner, column.Name));

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
                DedupeKeyFor(owner, null),
                match.Score,
                // 路徑指向<b>物件</b>，不是資料行：四段式名稱的第一段是連結伺服器，
                // 把資料行接成第四段會讓下游把資料庫名讀成伺服器名。
                PathFor(owner),
                column.Name,
                match.Spans,
                new SqlCatalogSearchTarget(
                    index.DatabaseName, owner.SchemaName, owner.Name, owner.Kind, owner.ObjectId, column.Name),
                badges);

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
        SearchExamineCounter counter,
        IReadOnlyList<SearchBadge> badges,
        CancellationToken cancellationToken)
    {
        if (index.Definitions is not { } definitions)
        {
            return true;
        }

        foreach (var info in index.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (definitions.For(info.ObjectId) is not { } definition)
            {
                continue;
            }

            var categoryId = SqlCatalogSearchCategories.IdFor(info.Kind);

            if (categoryId is null || !query.MatchesCategory(categoryId))
            {
                continue;
            }

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
                SearchMatchTarget.Text,
                info.QualifiedName,
                key,
                // 本文的分數是「提到幾次」。與名稱那一組不同尺度沒有關係：
                // SearchMatchTarget 已經把兩組分開排，兩邊的分數不會互相比較。
                matches.Count,
                PathFor(info),
                snippet,
                spans,
                new SqlCatalogSearchTarget(
                    index.DatabaseName, info.SchemaName, info.Name, info.Kind, info.ObjectId),
                badges);

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
    /// 使用者要挑的時候，清單走 <see cref="SqlCatalogSearchDatabases.TryList"/>，
    /// 而那一條只列名稱，不建任何索引。
    ///
    /// 指名了就換目錄，走 <see cref="SqlDatabaseScopedConnectionSource"/>：查詢一律寫成
    /// 不加限定的 <c>sys.</c>，決定查哪一個資料庫的是連線。換不過去（資料庫不存在、
    /// 離線、沒有權限）時開連線會丟 <see cref="System.Data.Common.DbException"/>，
    /// 由索引那一層降級成「這一輪沒有這個資料庫的資料」——<b>絕不</b>退回拿目前連線裡
    /// 同名的物件回答，那比什麼都不做糟，什麼都不做至少是沉默。
    ///
    /// 同一個名稱指名兩次只掃一次：兩份結果一模一樣，而去重是在聚合器那一端付的錢。
    ///
    /// 指名了伺服器就整輪不回結果：沒有連結伺服器（四段式名稱）的索引，
    /// 而拿本機的東西當成對面那台的答案正是上一段禁止的事。連結伺服器要的是
    /// <c>SqlCatalogQualifier</c> 與 <c>OPENQUERY</c> 那一條路，不是換個資料庫就行。
    /// </remarks>
    private IReadOnlyList<ISqlConnectionSource> ResolveSources(SearchScope scope)
    {
        if (scope.Servers.Count > 0)
        {
            return Array.Empty<ISqlConnectionSource>();
        }

        if (scope.Databases.Count == 0)
        {
            return new[] { _connectionSource };
        }

        var sources = new List<ISqlConnectionSource>(scope.Databases.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var databaseName in scope.Databases)
        {
            if (databaseName.Length == 0 || !seen.Add(databaseName))
            {
                continue;
            }

            sources.Add(
                string.Equals(databaseName, _connectionSource.DatabaseName, StringComparison.OrdinalIgnoreCase)
                    ? _connectionSource
                    : new SqlDatabaseScopedConnectionSource(_connectionSource, databaseName));
        }

        return sources;
    }

    /// <summary>
    /// 跨 provider 穩定的去重鍵：資料庫、結構描述、名稱，資料行再加一段。
    /// </summary>
    /// <remarks>
    /// 資料庫名稱非有不可：兩個資料庫裡各有一張 <c>Loan</c> 是常態，少了這一段
    /// 其中一列會被去重吃掉，而使用者看不出少了哪一個。
    ///
    /// 不含命中部位：同一個物件被名稱與定義本文同時命中時，聚合器要把它們併成一列，
    /// 靠的就是兩邊寫出同一個鍵。<b>資料行命中也走物件那一份鍵</b>（<paramref name="columnName"/>
    /// 傳 null）——一張表有三個資料行對上時，使用者要的是一列 <c>Frm_Acceptance</c> 加上
    /// 「命中了這三行」，不是三列同一張表。接了資料行的那一份只剩一個用途：
    /// <see cref="SearchExamineCounter"/> 的續掃位置，那裡問的是「掃到哪一行」。
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

    /// <summary>一個資料庫這一輪掃到哪裡；只有那一條執行緒讀寫它。</summary>
    private sealed class DatabaseRound
    {
        internal DatabaseRound(string databaseName)
        {
            DatabaseName = databaseName;
        }

        /// <summary>讀不到時要寫進那一句話裡的名稱；沒有它的話使用者不知道該去看哪一個。</summary>
        internal string DatabaseName { get; }

        internal bool Truncated { get; set; }

        /// <summary>這個資料庫這一輪整個讀不到；與 <see cref="Truncated"/> 是兩句話。</summary>
        internal bool Unavailable { get; set; }

        /// <summary>
        /// 讀不到的結構化原因；<see cref="Unavailable"/> 為 false 時無意義。
        /// </summary>
        /// <remarks>
        /// 一個資料庫一份，不是整個 provider 一份：勾了五個資料庫時，
        /// 「其中一個沒權限」與「五個都沒權限」要說的話不一樣，而合成一份就分不出來了。
        /// </remarks>
        internal SearchUnavailableKind UnavailableKind { get; set; }

        internal string? Checkpoint { get; set; }
    }
}
