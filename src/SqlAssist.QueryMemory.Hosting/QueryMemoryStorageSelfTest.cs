using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Hosting;

/// <summary>宿主與封裝工具共用的診斷；只保存內建文字，不接觸編輯器或 SQL Server。</summary>
public static class QueryMemoryStorageSelfTest
{
    public const string ReportFileName = "report.txt";

    // 刪除收藏不連帶刪版本，這份 SQL 會活到下一輪維護，容量驗證必須把它算進去。
    private const string FavoriteEditSql = "SELECT * FROM Lib_Reader WHERE ReaderId = 1;";
    private const string RecoverySql = "SELECT CopyNo FROM Cat_BookCopy;";

    public static Task RunAsync(string runDirectory, string? ssmsIdeDirectory, CancellationToken cancellationToken) =>
        Task.Run(() => RunCoreAsync(runDirectory, ssmsIdeDirectory, cancellationToken), cancellationToken);

    private static async Task RunCoreAsync(string runDirectory, string? ssmsIdeDirectory, CancellationToken token)
    {
        Directory.CreateDirectory(runDirectory);
        // 每次由呼叫端指定唯一目錄；CreateNew 防止重跑覆寫上次的診斷證據。
        using var report = new StreamWriter(new FileStream(Path.Combine(runDirectory, ReportFileName),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var timer = Stopwatch.StartNew();
        var database = Path.Combine(runDirectory, "self-test.db");
        try
        {
            using var process = Process.GetCurrentProcess();
            report.WriteLine($"Query Memory 儲存自我測試 | {DateTimeOffset.UtcNow:O}");
            report.WriteLine($"程序：{process.ProcessName} ({process.Id})；x64：{Environment.Is64BitProcess}；CLR：{Environment.Version}");
            report.WriteLine($"Hosting：{typeof(QueryMemoryStorageSelfTest).Assembly.FullName}");
            report.WriteLine($"Hosting 建置：{typeof(QueryMemoryStorageSelfTest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
            report.WriteLine($"Hosting 路徑：{typeof(QueryMemoryStorageSelfTest).Assembly.Location}");
            report.WriteLine($"宿主目錄：{ssmsIdeDirectory ?? "獨立測試"}");
            report.WriteLine("僅使用內建範例 SQL；未擷取編輯器內容、未連接 SQL Server。");
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("自我測試必須在 x64 程序執行。");
            if (File.Exists(database)) throw new IOException("測試資料庫已存在，拒絕覆寫。");
            var before = ProviderAssemblies();
            report.WriteLine("宿主原有 provider：" + (before.Length == 0 ? "無" : string.Join(" | ", before)));
            var document = new QueryDocument(Guid.NewGuid(), "Library.sql", null);
            var session = new QuerySession(Guid.NewGuid(), document.DocumentId, DateTimeOffset.UtcNow);
            const string sql = "SELECT * FROM Lib_Reader;";
            var contentId = QueryContent.Create(sql).ContentId;
            var policy = new QueryMemoryPolicy(false, false, TimeSpan.FromMinutes(10), true, false);
            var favoriteId = Guid.NewGuid();
            var lease = "";
            // 逐秒遞增的擷取時間讓筆數配額有明確界線；同一毫秒的執行會整批保留而驗不到配額。
            var start = DateTimeOffset.UtcNow;

            using (var repository = await IsolatedQueryMemoryRepository.OpenAsync(database, ssmsIdeDirectory, token).ConfigureAwait(false))
            {
                report.WriteLine(await repository.ProbeAsync(token).ConfigureAwait(false));
                var engine = new QueryRevisionEngine();
                var processor = new QueryMemoryProcessor(repository, engine);
                for (var i = 1; i <= 20; i++)
                    await processor.ProcessAsync(new QueryMemoryCapture(Guid.NewGuid(), document, session, i,
                        start.AddSeconds(i), QueryCaptureKind.BeforeExecute, new QueryTextSnapshot(sql)), policy, token).ConfigureAwait(false);
                var state = await repository.ReadSessionAsync(session.SessionId, token).ConfigureAwait(false);
                var write = engine.Prepare(new QueryMemoryCapture(Guid.NewGuid(), document, session, 21,
                    start.AddSeconds(21), QueryCaptureKind.BeforeExecute, new QueryTextSnapshot(sql)), state, policy)
                    ?? throw new InvalidOperationException("未產生測試交易。");
                Require(await repository.CommitAsync(write, null, token).ConfigureAwait(false) == QueryMemoryCommitResult.Committed, "首次提交");
                Require(await repository.CommitAsync(write, null, token).ConfigureAwait(false) == QueryMemoryCommitResult.AlreadyCommitted, "冪等重送");
                report.WriteLine("通過：21 次執行與冪等重送。");
                var revisionId = write.State.LatestRevision?.RevisionId
                    ?? throw new InvalidOperationException("自我測試缺少完整 SQL 版本。");
                var favorite = new FavoriteQuery(favoriteId, "讀者查詢", null, revisionId, FavoriteQueryScope.Global, null);
                Require(await repository.WriteFavoriteQueryAsync(new FavoriteQueryWrite(favorite), token).ConfigureAwait(false) == FavoriteQueryWriteResult.Committed, "新增 Favorite Query");
            }
            report.WriteLine("通過：第一次卸載隔離 AppDomain。");
            token.ThrowIfCancellationRequested();
            using (var reopened = await IsolatedQueryMemoryRepository.OpenAsync(database, ssmsIdeDirectory, token).ConfigureAwait(false))
            {
                var page = await reopened.ReadHistoryAsync(new QueryHistoryRequest(50, QueryHistoryKind.Executed), token).ConfigureAwait(false);
                Require(page.Items.Count == 21 && page.NextCursor == null && page.Items.All(item => item.ContentId == contentId), "重新開啟與內容去重");
                Require((await reopened.ReadContentAsync(contentId, token).ConfigureAwait(false))?.SqlText == sql, "全文還原");
                report.WriteLine("通過：重新開啟、21 筆紀錄共用內容位址、全文還原。");
                var favorite = await reopened.ReadFavoriteQueryAsync(favoriteId, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Favorite Query 重新開啟後遺失。");
                Require(favorite.ContentId == contentId, "Favorite Query 共用內容");
                var changed = favorite.Query with { Name = "讀者收藏", Scope = FavoriteQueryScope.Database,
                    Connection = new QueryConnectionContext("LibraryServer", "Library") };
                Require(await reopened.WriteFavoriteQueryAsync(new FavoriteQueryWrite(changed, favorite.Version), token).ConfigureAwait(false) == FavoriteQueryWriteResult.Committed, "更新 Favorite Query");
                Require(await reopened.DeleteFavoriteQueryAsync(favoriteId, favorite.Version, token).ConfigureAwait(false) == FavoriteQueryWriteResult.Conflict, "Favorite Query 過期版本保護");
                var favoritePage = await reopened.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(1, FavoriteQueryScope.Database, "LibraryServer", "Library"), token).ConfigureAwait(false);
                Require(favoritePage.Items.Count == 1 && favoritePage.Items[0].Query == changed && favoritePage.NextCursor == null, "Favorite Query scope 分頁");
                async Task<int> SearchFavoriteAsync(string search) =>
                    (await reopened.ReadFavoriteQueriesAsync(new FavoriteQueryRequest(5, FavoriteQueryScope.Database,
                        "LibraryServer", "Library", search), token).ConfigureAwait(false)).Items.Count;
                // 說明為 null 的收藏靠 SQL 全文命中；大小寫不同的字串不得比對成功。
                Require(await SearchFavoriteAsync("Lib_Reader").ConfigureAwait(false) == 1, "Favorite Query SQL 全文搜尋");
                Require(await SearchFavoriteAsync("讀者收藏").ConfigureAwait(false) == 1, "Favorite Query 名稱搜尋");
                Require(await SearchFavoriteAsync("lib_reader").ConfigureAwait(false) == 0, "Favorite Query 搜尋區分大小寫");
                var current = await VerifyFavoriteEditAsync(reopened, favoriteId, start, token).ConfigureAwait(false);
                report.WriteLine("通過：Favorite Query 改 SQL 建立新版本、不進 History，配額只留最新版本。");
                Require(await reopened.DeleteFavoriteQueryAsync(favoriteId, current, token).ConfigureAwait(false) == FavoriteQueryWriteResult.Committed, "刪除 Favorite Query");
                Require(await reopened.ReadFavoriteQueryAsync(favoriteId, token).ConfigureAwait(false) == null, "Favorite Query 已刪除");
                Require((await reopened.ReadContentAsync(contentId, token).ConfigureAwait(false))?.SqlText == sql, "刪除 Favorite 不刪除 History 內容");
                report.WriteLine("通過：Favorite Query CRUD、scope 分頁、搜尋、版本衝突與刪除後歷史保留。");
                await VerifyMaintenanceAsync(reopened, contentId, sql, token).ConfigureAwait(false);
                report.WriteLine("通過：有界維護續跑、筆數配額、容量量測與無法回收時保護 Session head／Recovery。");
                lease = await VerifySessionLeaseAsync(reopened, document, start, token).ConfigureAwait(false);
                report.WriteLine("通過：Session 心跳租約在租約還在時保護未存檔草稿，期限到了也不回收。");
            }
            token.ThrowIfCancellationRequested();
            using (var reaper = await IsolatedQueryMemoryRepository.OpenAsync(database, ssmsIdeDirectory, token).ConfigureAwait(false))
            {
                await VerifyLeaseReclaimAndCompactionAsync(reaper, lease, token).ConfigureAwait(false);
                report.WriteLine("通過：回收失效租約後才清除未存檔草稿，WAL 截斷與整理保留其餘內容。");
                await VerifyScheduledMaintenanceAsync(reaper, token).ConfigureAwait(false);
                report.WriteLine("通過：設定驅動的排程、心跳與保留分級跑完整輪，游標沒有被政策換掉而作廢。");
            }
            using (File.Open(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            report.WriteLine("通過：最後一次卸載與資料庫檔案釋放（不代表 native DLL 已從程序卸載）。");
            var added = ProviderAssemblies().Except(before, StringComparer.Ordinal).ToArray();
            Require(added.Length == 0, "宿主 AppDomain 未新增 provider：" + string.Join(" | ", added));
            report.WriteLine("通過：宿主 AppDomain 未新增 SQLite provider。");
            report.WriteLine($"PASS | {timer.ElapsedMilliseconds} ms；仍需人工確認宿主功能共存與重啟。");
        }
        catch (Exception error)
        {
            // 診斷留下最後成功步驟及失敗堆疊；重新拋出，避免 UI 把產出報告誤認成測試通過。
            report.WriteLine($"FAIL | {timer.ElapsedMilliseconds} ms | {error}");
            throw;
        }
    }

    /// <summary>
    /// 宿主真正會跑的那一條：設定 → 保留分級 → 排程 → 一次有界維護。
    /// </summary>
    /// <remarks>
    /// 這裡驗的是組裝，不是清理本身（那由上面的 <c>DrainAsync</c> 涵蓋）。最重要的一項是
    /// 游標：保留分級的截止時間由「現在」換算，每一批都重算的話，同一輪的第二批就會
    /// 帶著舊游標碰上新政策而被儲存層拒絕——那是只有跑完整輪才看得出來的接線錯誤。
    /// </remarks>
    private static async Task VerifyScheduledMaintenanceAsync(IsolatedQueryMemoryRepository repository,
        CancellationToken token)
    {
        var owner = new QueryMemoryLeaseOwner(Environment.MachineName, CurrentProcessId(), CurrentProcessStart());
        var heartbeat = new QueryMemoryLeaseHeartbeat(repository, owner, TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;
        var opened = await heartbeat.BeatAsync(now, token).ConfigureAwait(false);
        Require(await heartbeat.BeatAsync(now.AddMinutes(1), token).ConfigureAwait(false) == opened, "心跳續用同一個租約");

        // 期限短、配額小，一輪之內真的有東西可以回收；容量不設上限，壓力分級不該被觸發。
        var plan = new QueryMemoryRetentionPlan(TimeSpan.FromDays(1), TimeSpan.FromDays(1),
            TimeSpan.FromDays(1), null, 1, 1, 1);
        var schedule = new QueryMemoryMaintenanceSchedule(now, TimeSpan.Zero, TimeSpan.FromMinutes(60),
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        var runner = new QueryMemoryMaintenanceRunner(repository, repository,
            new QueryMemoryLeaseReaper(Environment.MachineName, IsOwnerRunning), owner, schedule, plan,
            TimeSpan.FromMinutes(10), candidateLimit: 4);

        for (var tick = 1; tick <= 60; tick++)
        {
            token.ThrowIfCancellationRequested();
            // 每一批都往前五分鐘：排程說可以跑，游標才輪得到下一段。
            var result = await runner.RunOnceAsync(now.AddMinutes(5 * tick), hostIdle: false,
                sessionHeartbeatActive: true, token).ConfigureAwait(false);
            Require(result.Outcome == QueryMemoryMaintenanceOutcome.Maintained, "排程取得維護租約");
            Require(result.Level == 0, "容量未超限時不進入壓力分級");
            if (runner.PendingWork) continue;

            var state = await repository.ReadMaintenanceStateAsync(token).ConfigureAwait(false);
            Require(state != null && state.Cursor == null && state.Round.PlanFingerprint == plan.Fingerprint,
                "維護輪次寫回共用狀態表");
            // 正常卸載交回租約，別的程序不必等十分鐘過期才能接續同一份狀態。
            Require(await runner.StopAsync(token).ConfigureAwait(false), "停止時交回維護租約");
            Require(await repository.TryAcquireMaintenanceLeaseAsync(owner with { ProcessId = owner.ProcessId + 1 },
                now, now.AddMinutes(-10), token).ConfigureAwait(false), "交回後其他程序立即取得維護租約");
            return;
        }

        throw new InvalidOperationException("排程維護未在自我測試上限內收斂。");
    }

    private static int CurrentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        return process.Id;
    }

    private static DateTimeOffset CurrentProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime;
    }

    /// <summary>回傳改 SQL 之後的版本 token；呼叫端不得沿用編輯前讀到的那一份。</summary>
    private static async Task<Guid> VerifyFavoriteEditAsync(IsolatedQueryMemoryRepository repository, Guid favoriteId,
        DateTimeOffset start, CancellationToken token)
    {
        const string first = "SELECT * FROM Lib_Reader WHERE ReaderId > 0;";
        const string second = FavoriteEditSql;
        var before = await repository.ReadFavoriteQueryAsync(favoriteId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Favorite Query 遺失。");
        var edit = new FavoriteQueryEdit(favoriteId, before.Version, Guid.NewGuid(), first, start.AddSeconds(100));
        Require(await repository.EditFavoriteQuerySqlAsync(edit, token).ConfigureAwait(false) == FavoriteQueryWriteResult.Committed, "Favorite Query 改 SQL");
        Require(await repository.EditFavoriteQuerySqlAsync(edit, token).ConfigureAwait(false) == FavoriteQueryWriteResult.Conflict, "Favorite Query 改 SQL 過期版本保護");
        var edited = await repository.ReadFavoriteQueryAsync(favoriteId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Favorite Query 編輯後遺失。");
        Require(edited.Query.CurrentRevisionId == edit.RevisionId && edited.ContentId == QueryContent.Create(first).ContentId, "Favorite Query 換到新版本");
        Require(edited.Query with { CurrentRevisionId = before.Query.CurrentRevisionId } == before.Query, "改 SQL 不動名稱或 scope");
        Require((await repository.ReadHistoryAsync(new QueryHistoryRequest(50, QueryHistoryKind.All, first), token)
            .ConfigureAwait(false)).Items.Count == 0, "Favorite Query 編輯不進 History");
        var again = new FavoriteQueryEdit(favoriteId, edited.Version, Guid.NewGuid(), second, start.AddSeconds(200));
        Require(await repository.EditFavoriteQuerySqlAsync(again, token).ConfigureAwait(false) == FavoriteQueryWriteResult.Committed, "Favorite Query 再次改 SQL");
        // 每 Favorite 版本配額只留最新一版；目前版本與擷取產生的版本都不受影響。
        await DrainAsync(repository, new QueryMemoryMaintenancePolicy(null, null, null, null, null, 1), token).ConfigureAwait(false);
        Require(await repository.ReadContentAsync(QueryContent.Create(first).ContentId, token).ConfigureAwait(false) == null, "配額回收舊版本");
        var kept = await repository.ReadFavoriteQueryAsync(favoriteId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("配額後 Favorite Query 遺失。");
        Require(kept.Query.CurrentRevisionId == again.RevisionId && kept.ContentId == QueryContent.Create(second).ContentId, "配額保留目前版本");
        return kept.Version;
    }

    private static async Task VerifyMaintenanceAsync(IsolatedQueryMemoryRepository repository, string contentId, string sql, CancellationToken token)
    {
        var usage = await repository.ReadUsageAsync(token).ConfigureAwait(false);
        Require(usage.ContentBytes == 2L * (sql.Length + FavoriteEditSql.Length) && usage.DatabaseFileBytes > 0, "邏輯容量與實體檔案量測");
        // 只給筆數配額、不給截止時間：期限內但超額的執行也要回收，head 內容仍受保護。
        await DrainAsync(repository, new QueryMemoryMaintenancePolicy(null, null, null, 5, 0), token).ConfigureAwait(false);
        Require((await repository.ReadHistoryAsync(new QueryHistoryRequest(50, QueryHistoryKind.Executed), token).ConfigureAwait(false)).Items.Count == 5,
            "Execution 筆數配額");
        Require((await repository.ReadContentAsync(contentId, token).ConfigureAwait(false))?.SqlText == sql, "筆數配額不刪除 Session head 內容");
        var result = await DrainAsync(repository, new QueryMemoryMaintenancePolicy(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, 0), token).ConfigureAwait(false);
        Require(result.CapacityStatus == QueryMemoryCapacityStatus.CannotReclaimWithinPolicy, "受保護內容無法回收");
        Require((await repository.ReadContentAsync(contentId, token).ConfigureAwait(false))?.SqlText == sql, "維護保留 head／Recovery 內容");
        Require((await repository.ReadHistoryAsync(new QueryHistoryRequest(1, QueryHistoryKind.Executed), token).ConfigureAwait(false)).Items.Count == 0,
            "維護清除過期執行");
    }

    /// <summary>寫進一份未存檔草稿並回傳本程序的租約識別碼；下一個 repository 才驗得到跨程序回收。</summary>
    private static async Task<string> VerifySessionLeaseAsync(IsolatedQueryMemoryRepository repository,
        QueryDocument document, DateTimeOffset start, CancellationToken token)
    {
        // 用另一台機器當擁有者：跨機器只認過期，同一次自我測試就能確定地走完回收那條路。
        var owner = new QueryMemoryLeaseOwner(Environment.MachineName + "-OFFLINE", Process.GetCurrentProcess().Id, start);
        var lease = await repository.OpenLeaseAsync(owner, start, token).ConfigureAwait(false);
        Require(await repository.RenewLeaseAsync(start.AddSeconds(1), token).ConfigureAwait(false), "續心跳");
        var session = new QuerySession(Guid.NewGuid(), document.DocumentId, start);
        var drafts = new QueryMemoryPolicy(true, false, TimeSpan.FromMinutes(10), false, true);
        await new QueryMemoryProcessor(repository, new QueryRevisionEngine(), leaseId: () => lease).ProcessAsync(
            new QueryMemoryCapture(Guid.NewGuid(), document, session, 1, start.AddSeconds(300),
                QueryCaptureKind.DraftIdle, new QueryTextSnapshot(RecoverySql)), drafts, token).ConfigureAwait(false);
        await DrainAsync(repository, RecoveryExpired(), token).ConfigureAwait(false);
        Require(await repository.ReadContentAsync(QueryContent.Create(RecoverySql).ContentId, token).ConfigureAwait(false) != null,
            "租約保護未存檔草稿");
        return lease;
    }

    private static async Task VerifyLeaseReclaimAndCompactionAsync(IsolatedQueryMemoryRepository repository,
        string lease, CancellationToken token)
    {
        var recoveryContent = QueryContent.Create(RecoverySql).ContentId;
        Require(await repository.ReadContentAsync(recoveryContent, token).ConfigureAwait(false) != null, "重新開啟後草稿仍在");
        var reaper = new QueryMemoryLeaseReaper(Environment.MachineName, IsOwnerRunning);
        using var current = Process.GetCurrentProcess();
        var alive = new QueryMemoryLease(lease,
            new QueryMemoryLeaseOwner(Environment.MachineName, current.Id, current.StartTime), DateTimeOffset.UtcNow);
        // 還在執行的本機程序永遠不該被判成可回收，否則使用者正在編輯的內容會消失。
        Require(reaper.Reclaimable(new[] { alive }).Count == 0, "還活著的程序不回收");
        var expired = await repository.ReadExpiredLeasesAsync(DateTimeOffset.UtcNow, 10, token).ConfigureAwait(false);
        Require(expired.Count == 1 && expired[0].LeaseId == lease, "讀出過期租約");
        var reclaimable = reaper.Reclaimable(expired);
        Require(reclaimable.Count == 1 && reclaimable[0] == lease, "確認程序已不存在");
        Require(await repository.ReleaseLeasesAsync(reclaimable, DateTimeOffset.UtcNow, token).ConfigureAwait(false) == 1, "釋放失效租約");
        await DrainAsync(repository, RecoveryExpired(), token).ConfigureAwait(false);
        Require(await repository.ReadContentAsync(recoveryContent, token).ConfigureAwait(false) == null, "回收未存檔草稿");
        var checkpoint = await repository.CheckpointAsync(token).ConfigureAwait(false);
        Require(checkpoint.Truncated && checkpoint.Usage.WalFileBytes == 0, "背景 checkpoint 截斷 WAL");
        var compacted = await repository.CompactAsync(token).ConfigureAwait(false);
        Require(compacted.DatabaseFileBytes > 0 && compacted.WalFileBytes == 0, "手動整理後資料庫仍可用");
        Require(compacted.DatabaseFileBytes <= checkpoint.Usage.DatabaseFileBytes, "手動整理不會放大資料庫");
    }

    /// <summary>連未存檔草稿都過期的政策；Recovery 期限是獨立的一個，不跟著草稿期限走。</summary>
    private static QueryMemoryMaintenancePolicy RecoveryExpired() =>
        new(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, null, null, null, null, DateTimeOffset.MaxValue);

    private static bool IsOwnerRunning(QueryMemoryLeaseOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return QueryMemoryLeaseReaper.IsSameProcess(owner, process.StartTime);
        }
        // 沒有這個 PID 或程序已結束才算不在；問不到細節（例如存取被拒）一律當成還活著。
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    private static async Task<QueryMemoryMaintenanceResult> DrainAsync(IsolatedQueryMemoryRepository repository,
        QueryMemoryMaintenancePolicy policy, CancellationToken token)
    {
        string? cursor = null;
        for (var batch = 0; batch < 40; batch++)
        {
            token.ThrowIfCancellationRequested();
            var result = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 4, cursor), token).ConfigureAwait(false);
            // 每個候選最多刪本體與投影兩列，每列再帶出一個內容與一個連線。
            Require(result.ExaminedCandidates <= 4 && result.DeletedRows <= 24, "清理工作量上限");
            if (result.Cursor == null && !result.RequiresAnotherPass) return result;
            cursor = result.Cursor;
        }
        throw new InvalidOperationException("維護未在自我測試上限內收斂。");
    }

    private static string[] ProviderAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name?.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) == true || a.GetName().Name == "Microsoft.Data.Sqlite")
        .Select(a => a.FullName + " @ " + a.Location).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static void Require(bool condition, string step)
    {
        if (!condition) throw new InvalidOperationException("驗證失敗：" + step);
    }
}
