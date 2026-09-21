using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// SQL Agent 作業搜尋：作業名稱、步驟名稱與步驟命令本文。
/// </summary>
/// <remarks>
/// 這個來源與 <see cref="SqlCatalogSearchProvider"/> 刻意長得不一樣，三件事都不同：
/// <b>跨的是伺服器不是資料庫</b>（快取鍵是 <see cref="ISqlConnectionSource.ServerCacheKey"/>），
/// <b>資料在 <c>msdb</c></b>（查詢寫三段式名稱，不換目錄），
/// <b>權限不足是常態</b>（多數登入對 <c>msdb</c> 沒有 <c>SELECT</c>）。
/// 契約本身一個字都沒有改：分類自己宣告、酬載自己一型、預算與串流照同一套。
///
/// <b>權限不足降級成「這個來源這一輪沒有資料」，不是錯誤也不是空白。</b>
/// 錯誤（讓 <see cref="System.Data.Common.DbException"/> 冒出去）會被聚合器記成
/// <see cref="SearchProviderFailure"/>，而工具窗的頁尾會被一句紅字佔住——
/// 對一個「本來就多半讀不到」的來源，那等於每一次搜尋都在報錯。
/// 空白（什麼都不說）更糟：與「這台伺服器上真的沒有叫這個名字的作業」一模一樣。
/// 走的是中間那條：<see cref="ISearchSink.ReportUnavailable(string)"/> 帶上這個來源
/// 自己寫的那一句話，整輪標記成部分結果，而呼叫端原樣把它貼在狀態列上。
/// </remarks>
public sealed class SqlAgentJobSearchProvider : ISearchProvider
{
    /// <summary>跨版本穩定的識別字；分類 Id 與使用者偏好都以它為前綴。</summary>
    public const string ProviderId = "agent-job";

    /// <summary>
    /// 讀不到 <c>msdb</c> 時交給呼叫端貼在狀態列上的那一句話。
    /// </summary>
    /// <remarks>
    /// 由這個來源自己寫，不是呈現那一層照 provider Id 查一張表：查表的那一版每多一個
    /// 「權限常常不足」的來源就要在同一處多一個 <c>if</c>，而寫得出這一句的只有
    /// 知道自己去讀了 <c>msdb</c> 的這裡。
    ///
    /// 括號裡寫的是最可能的原因而不是斷言：連不上與逾時也走同一條降級路徑，
    /// 而斷言權限的話，使用者會去查一個好好的權限設定。伺服器真的給了權限錯誤碼時
    /// 才換成 <see cref="DeniedReason"/>——那一句斷言得起。
    /// </remarks>
    private const string UnavailableReason =
        "SQL Agent 作業這一輪讀不到（多半是這個登入對 msdb 沒有權限），這個來源沒有結果。";

    /// <summary>伺服器明說是權限時的那一句；「多半」換成斷言。</summary>
    private const string DeniedReason =
        "SQL Agent 作業這一輪讀不到（這個登入對 msdb 沒有權限），這個來源沒有結果。";

    /// <summary>去重鍵的前綴；與其他 provider 的鍵不會互相碰撞。</summary>
    private const string DedupePrefix = "agent-job|";

    private readonly ISqlConnectionSource _connectionSource;
    private readonly SqlAgentJobSearchSnapshotCache _snapshotCache;

    /// <param name="snapshotCache">
    /// 作業快照的快取；不給時自己建一份。同一個工具窗的多個 provider 實例要共用同一份時
    /// 由呼叫端傳進來——各自持有一份的症狀是每一輪搜尋都向 <c>msdb</c> 撈一次。
    /// </param>
    public SqlAgentJobSearchProvider(
        ISqlConnectionSource connectionSource,
        SqlAgentJobSearchSnapshotCache? snapshotCache = null)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
        _snapshotCache = snapshotCache ?? new SqlAgentJobSearchSnapshotCache();
        Categories = SqlAgentJobSearchCategories.Create(ProviderId);
    }

    public string Id => ProviderId;

    public string DisplayName => "SQL Agent 作業";

    public IReadOnlyList<SearchCategory> Categories { get; }

    /// <summary>這個 provider 用的快照快取；重新整理時整批丟掉用得到。</summary>
    public SqlAgentJobSearchSnapshotCache SnapshotCache => _snapshotCache;

    /// <remarks>
    /// 走一次 <see cref="Task.Run(Action, CancellationToken)"/>，與目錄那一邊同一個理由：
    /// 撈快照與掃描都是同步的阻塞工作，而聚合器是直接 await 每一個 provider 的——
    /// 留在呼叫端的執行緒上跑完的話，幾個來源會變成一個接一個。
    /// </remarks>
    public Task SearchAsync(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (sink is null) throw new ArgumentNullException(nameof(sink));

        // 指名了伺服器就整輪不回結果。手上這條連線是工具窗選的那一台，拿它的作業回答
        // 「另一台有哪些作業」正是明文禁止的那種退路——而退過在畫面上看不出來。
        if (query.Scope.Servers.Count > 0) return Task.CompletedTask;

        return Task.Run(() => Scan(query, sink, cancellationToken), cancellationToken);
    }

    /// <remarks>
    /// <b>資料庫範圍不縮小這個來源。</b><see cref="SearchScope.Databases"/> 講的是
    /// 「搜哪幾個資料庫的物件」，而作業不住在任何一個使用者資料庫裡。
    /// 拿 <c>sysjobsteps.database_name</c> 去對它看起來很合理，但那一欄只有 <c>TSQL</c>
    /// 子系統填得出來——CmdExec 與 PowerShell 步驟一律是空的，照它過濾等於把那些步驟
    /// 整組藏掉，而畫面上看不出被藏過。要拿掉作業，使用者勾的是分類 pill。
    /// 步驟跑在哪一個資料庫仍然看得到，掛在膠囊上。
    /// </remarks>
    private void Scan(SearchQuery query, ISearchSink sink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var wantsJobs = query.MatchesCategory(SqlAgentJobSearchCategories.JobCategoryId);
        var wantsSteps = query.MatchesCategory(SqlAgentJobSearchCategories.StepCategoryId);

        // 兩顆 pill 都被勾掉時整輪不必付任何代價。靠聚合器那道最後防線的話，
        // 這一輪仍然會向 msdb 撈一次全部的作業，只為了讓每一筆結果被靜靜丟掉。
        if (!wantsJobs && !wantsSteps) return;

        var wantsName = query.IncludesTarget(SearchMatchTarget.Name);

        // 空輸入的本文比對沒有意義（每一個位置都命中），而它正是最貴的那一段。
        var wantsText = wantsSteps && !query.IsEmpty && query.IncludesTarget(SearchMatchTarget.Text);

        if (!wantsName && !wantsText) return;

        var counter = new SearchExamineCounter(sink);
        var truncated = false;

        try
        {
            // 撈一次快照要兩條跨資料庫的查詢；預算已經滿了就別付這個代價。
            if (sink.IsExhausted)
            {
                truncated = true;
                return;
            }

            var snapshot = _snapshotCache.GetOrLoad(
                _connectionSource, wantsText, cancellationToken, out var unavailableKind);

            if (snapshot is null)
            {
                // 讀不到 msdb。這不是失敗（失敗會把頁尾整行佔住），也不是空白
                // （空白與「這台伺服器上沒有這個作業」一模一樣），更不是「沒掃完」
                // ——後者叫使用者縮小範圍，而那對沒有權限完全沒有用。
                var denied = unavailableKind == SearchUnavailableKind.Denied;
                sink.ReportUnavailable(denied ? DeniedReason : UnavailableReason, unavailableKind);
                return;
            }

            // 命令本文只收到一半也是「沒掃完」。不說的話，使用者看到的與
            // 「這個字串在這台伺服器的作業裡不存在」一模一樣。
            if (wantsText && !snapshot.CommandsComplete) truncated = true;

            if (wantsName && !ScanNames(snapshot, query, sink, counter, wantsJobs, wantsSteps, cancellationToken))
            {
                truncated = true;
                return;
            }

            if (wantsText && !ScanCommands(snapshot, query, sink, counter, cancellationToken))
            {
                truncated = true;
            }
        }
        finally
        {
            counter.Flush();
            if (truncated) sink.ReportTruncated(counter.Checkpoint);
        }
    }

    /// <remarks>
    /// 名稱命中先全部掃完再掃命令本文，與目錄那一邊同一個理由：名稱命中在使用者還在打字時
    /// 就要上畫面，而命令本文要把每一個步驟整份掃過。混在一起的話，預算會被前幾個作業的
    /// 命令吃掉，而後面那些名稱明明對得上的作業連比都沒比到。
    /// </remarks>
    private static bool ScanNames(
        SqlAgentJobSearchSnapshot snapshot,
        SearchQuery query,
        ISearchSink sink,
        SearchExamineCounter counter,
        bool wantsJobs,
        bool wantsSteps,
        CancellationToken cancellationToken)
    {
        foreach (var job in snapshot.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobBadges = BadgesFor(snapshot.ServerName, job, null);

            if (wantsJobs)
            {
                var key = JobKey(snapshot.ServerName, job);
                counter.Note(key);

                // 名稱怎麼比與目錄那一邊同一份：沒開修飾是模糊比對，開了就是字面比對。
                var match = SearchIdentifierMatch.Match(query, job.Name);

                if (match.IsMatch)
                {
                    var hit = new SearchHit(
                        ProviderId,
                        SqlAgentJobSearchCategories.JobCategoryId,
                        SearchMatchTarget.Name,
                        job.Name,
                        key,
                        match.Score,
                        // 作業沒有限定名稱：它不在任何結構描述底下，也不屬於任何資料庫。
                        // 硬湊一條路徑的話，下游會把伺服器名讀成資料庫名。
                        path: null,
                        // 片段就是名稱本體：高亮區段的索引落在它上面。
                        job.Name,
                        match.Spans,
                        new SqlAgentJobSearchTarget(snapshot.ServerName, job.JobId, job.Name, job.IsEnabled),
                        jobBadges);

                    if (!sink.TryReport(hit)) return false;
                }
            }

            if (!wantsSteps) continue;

            foreach (var step in job.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = StepKeyOf(snapshot.ServerName, job, step);
                counter.Note(key);

                // 沒有取名的步驟不比對名稱：空字串對任何樣式都不會命中，而
                // 比對器對空候選的行為不該由這裡假設。
                if (step.Name.Length == 0) continue;

                var match = SearchIdentifierMatch.Match(query, step.Name);

                if (!match.IsMatch) continue;

                var hit = new SearchHit(
                    ProviderId,
                    SqlAgentJobSearchCategories.StepCategoryId,
                    SearchMatchTarget.Name,
                    StepTitle(job, step),
                    key,
                    match.Score,
                    path: null,
                    step.Name,
                    match.Spans,
                    TargetFor(snapshot.ServerName, job, step),
                    BadgesFor(snapshot.ServerName, job, step));

                if (!sink.TryReport(hit)) return false;
            }
        }

        return true;
    }

    /// <remarks>
    /// 本文命中只掛在<b>步驟</b>上，不掛在作業上：命中的是某一個步驟的那一段 SQL，
    /// 而作業本身沒有本文。掛到作業身上的症狀是使用者點下去，開出來的是二十個步驟，
    /// 而他找的那一段在第幾步看不出來。
    /// </remarks>
    private static bool ScanCommands(
        SqlAgentJobSearchSnapshot snapshot,
        SearchQuery query,
        ISearchSink sink,
        SearchExamineCounter counter,
        CancellationToken cancellationToken)
    {
        foreach (var job in snapshot.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var step in job.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (step.Command is not { Length: > 0 } command) continue;

                var key = StepKeyOf(snapshot.ServerName, job, step);
                counter.Note(key);

                // 本文比的是使用者打進去的原文，不是正規化後的樣式：後者一律小寫，
                // 拿它做區分大小寫的比對永遠比不中任何大寫的字。裁片段與找位置只有
                // SqlCatalogBodySearch 一份——步驟命令與模組定義在這件事上沒有差別。
                var matches = SqlCatalogBodySearch.FindAll(command, query.Text, query.Options);

                if (matches.Count == 0) continue;

                var snippet = SqlCatalogBodySearch.BuildSnippet(command, matches, query.Text.Length, out var spans);

                var hit = new SearchHit(
                    ProviderId,
                    SqlAgentJobSearchCategories.StepCategoryId,
                    SearchMatchTarget.Text,
                    StepTitle(job, step),
                    key,
                    // 本文的分數是「提到幾次」，與名稱那一組不同尺度沒有關係：
                    // SearchMatchTarget 已經把兩組分開排。
                    matches.Count,
                    path: null,
                    snippet,
                    spans,
                    TargetFor(snapshot.ServerName, job, step),
                    BadgesFor(snapshot.ServerName, job, step));

                if (!sink.TryReport(hit)) return false;
            }
        }

        return true;
    }

    /// <summary>清單上那一列的字：作業名稱加上第幾步。</summary>
    /// <remarks>
    /// 步驟名稱在一個作業裡不保證唯一，而且「載入」這種名字在十個作業裡都有；
    /// 只寫步驟名稱的症狀是清單上一排一模一樣的字，而使用者分不出它們屬於哪一個作業。
    /// <c>step_id</c> 一起寫進去，是因為它正是 SQL Agent 自己在作業屬性上顯示的順序。
    /// </remarks>
    private static string StepTitle(SqlAgentJob job, SqlAgentJobStep step)
    {
        var order = step.StepId.ToString(CultureInfo.InvariantCulture);

        return step.Name.Length == 0
            ? job.Name + " › 第 " + order + " 步"
            : job.Name + " › 第 " + order + " 步 " + step.Name;
    }

    private static SqlAgentJobSearchTarget TargetFor(string serverName, SqlAgentJob job, SqlAgentJobStep step) =>
        new(serverName, job.JobId, job.Name, job.IsEnabled,
            step.StepId, step.Name, step.Subsystem, step.DatabaseName);

    /// <summary>
    /// 跨 provider 穩定的去重鍵：伺服器、作業，步驟再加一段。
    /// </summary>
    /// <remarks>
    /// 伺服器那一段非有不可：工具窗的伺服器下拉會換，而兩台伺服器的 <c>msdb</c> 可以是
    /// 互相還原出來的，<c>job_id</c> 一模一樣。少了它，其中一列會被去重吃掉。
    ///
    /// 走 <c>job_id</c> 而不是作業名稱：名稱可以改，也可以同名
    /// （<c>sysjobs</c> 的唯一鍵是 job_id）。用名稱的症狀是兩個同名作業併成一列。
    ///
    /// 不含命中部位：同一個步驟被名稱與命令本文同時命中時，聚合器要把它們併成一列。
    /// </remarks>
    private static string JobKey(string serverName, SqlAgentJob job) =>
        DedupePrefix + serverName + "|" + job.JobId.ToString("D", CultureInfo.InvariantCulture);

    private static string StepKeyOf(string serverName, SqlAgentJob job, SqlAgentJobStep step) =>
        JobKey(serverName, job) + "|" + step.StepId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 這一列右下角的脈絡膠囊：哪一台、停用了沒、步驟跑在哪一個資料庫。
    /// </summary>
    /// <remarks>
    /// 伺服器那一顆永遠在：這個來源跨的正是伺服器，而清單上其他來源的列掛的是資料庫，
    /// 兩者擺在一起使用者才看得出這一列不跟著資料庫範圍走。
    ///
    /// 「已停用」只在停用時出現，不是每一列都掛一顆「已啟用」：後者是預設狀態，
    /// 每一列都寫一次等於把真正要注意的那幾列淹掉。
    /// </remarks>
    private static IReadOnlyList<SearchBadge> BadgesFor(
        string serverName, SqlAgentJob job, SqlAgentJobStep? step)
    {
        var badges = new List<SearchBadge>(3) { new(serverName, SearchBadge.ServerIcon) };

        if (!job.IsEnabled) badges.Add(new SearchBadge("已停用"));

        // database_name 只有 TSQL 子系統填得出來；空的時候不掛，掛一顆空膠囊
        // 會讓使用者以為那個步驟跑在一個沒有名字的資料庫上。
        if (step is { DatabaseName.Length: > 0 } named)
        {
            badges.Add(new SearchBadge(named.DatabaseName, SearchBadge.DatabaseIcon));
        }

        return badges.ToArray();
    }
}
