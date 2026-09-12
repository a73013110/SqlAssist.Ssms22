using System;
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
    private const string SavedEditSql = "SELECT * FROM Lib_Reader WHERE ReaderId = 1;";

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
            var savedId = Guid.NewGuid();
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
                Require(await repository.CommitAsync(write, token).ConfigureAwait(false) == QueryMemoryCommitResult.Committed, "首次提交");
                Require(await repository.CommitAsync(write, token).ConfigureAwait(false) == QueryMemoryCommitResult.AlreadyCommitted, "冪等重送");
                report.WriteLine("通過：21 次執行與冪等重送。");
                var revisionId = write.State.LatestRevision?.RevisionId
                    ?? throw new InvalidOperationException("自我測試缺少完整 SQL 版本。");
                var saved = new SavedQuery(savedId, "讀者查詢", null, revisionId, SavedQueryScope.Global, null, false);
                Require(await repository.WriteSavedQueryAsync(new SavedQueryWrite(saved), token).ConfigureAwait(false) == SavedQueryWriteResult.Committed, "新增 Saved Query");
            }
            report.WriteLine("通過：第一次卸載隔離 AppDomain。");
            token.ThrowIfCancellationRequested();
            using (var reopened = await IsolatedQueryMemoryRepository.OpenAsync(database, ssmsIdeDirectory, token).ConfigureAwait(false))
            {
                var page = await reopened.ReadHistoryAsync(new QueryHistoryRequest(50, QueryHistoryKind.Executed), token).ConfigureAwait(false);
                Require(page.Items.Count == 21 && page.NextCursor == null && page.Items.All(item => item.ContentId == contentId), "重新開啟與內容去重");
                Require((await reopened.ReadContentAsync(contentId, token).ConfigureAwait(false))?.SqlText == sql, "全文還原");
                report.WriteLine("通過：重新開啟、21 筆紀錄共用內容位址、全文還原。");
                var saved = await reopened.ReadSavedQueryAsync(savedId, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Saved Query 重新開啟後遺失。");
                Require(saved.ContentId == contentId, "Saved Query 共用內容");
                var changed = saved.Query with { Name = "讀者收藏", Pinned = true, Scope = SavedQueryScope.Database,
                    Connection = new QueryConnectionContext("LibraryServer", "Library") };
                Require(await reopened.WriteSavedQueryAsync(new SavedQueryWrite(changed, saved.Version), token).ConfigureAwait(false) == SavedQueryWriteResult.Committed, "更新 Saved Query");
                Require(await reopened.DeleteSavedQueryAsync(savedId, saved.Version, token).ConfigureAwait(false) == SavedQueryWriteResult.Conflict, "Saved Query 過期版本保護");
                var savedPage = await reopened.ReadSavedQueriesAsync(new SavedQueryRequest(1, SavedQueryScope.Database, "LibraryServer", "Library"), token).ConfigureAwait(false);
                Require(savedPage.Items.Count == 1 && savedPage.Items[0].Query == changed && savedPage.NextCursor == null, "Saved Query scope 分頁");
                async Task<int> SearchSavedAsync(string search) =>
                    (await reopened.ReadSavedQueriesAsync(new SavedQueryRequest(5, SavedQueryScope.Database,
                        "LibraryServer", "Library", search), token).ConfigureAwait(false)).Items.Count;
                // 說明為 null 的收藏靠 SQL 全文命中；大小寫不同的字串不得比對成功。
                Require(await SearchSavedAsync("Lib_Reader").ConfigureAwait(false) == 1, "Saved Query SQL 全文搜尋");
                Require(await SearchSavedAsync("讀者收藏").ConfigureAwait(false) == 1, "Saved Query 名稱搜尋");
                Require(await SearchSavedAsync("lib_reader").ConfigureAwait(false) == 0, "Saved Query 搜尋區分大小寫");
                var current = await VerifySavedEditAsync(reopened, savedId, start, token).ConfigureAwait(false);
                report.WriteLine("通過：Saved Query 改 SQL 建立新版本、不進 History，配額只留最新版本。");
                Require(await reopened.DeleteSavedQueryAsync(savedId, current, token).ConfigureAwait(false) == SavedQueryWriteResult.Committed, "刪除 Saved Query");
                Require(await reopened.ReadSavedQueryAsync(savedId, token).ConfigureAwait(false) == null, "Saved Query 已刪除");
                Require((await reopened.ReadContentAsync(contentId, token).ConfigureAwait(false))?.SqlText == sql, "刪除 Saved 不刪除 History 內容");
                report.WriteLine("通過：Saved Query CRUD、scope 分頁、搜尋、版本衝突與刪除後歷史保留。");
                await VerifyMaintenanceAsync(reopened, contentId, sql, token).ConfigureAwait(false);
                report.WriteLine("通過：有界維護續跑、筆數配額、容量量測與無法回收時保護 Session head／Recovery。");
            }
            using (File.Open(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            report.WriteLine("通過：第二次卸載與資料庫檔案釋放（不代表 native DLL 已從程序卸載）。");
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

    /// <summary>回傳改 SQL 之後的版本 token；呼叫端不得沿用編輯前讀到的那一份。</summary>
    private static async Task<Guid> VerifySavedEditAsync(IsolatedQueryMemoryRepository repository, Guid savedId,
        DateTimeOffset start, CancellationToken token)
    {
        const string first = "SELECT * FROM Lib_Reader WHERE ReaderId > 0;";
        const string second = SavedEditSql;
        var before = await repository.ReadSavedQueryAsync(savedId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Saved Query 遺失。");
        var edit = new SavedQueryEdit(savedId, before.Version, Guid.NewGuid(), first, start.AddSeconds(100));
        Require(await repository.EditSavedQuerySqlAsync(edit, token).ConfigureAwait(false) == SavedQueryWriteResult.Committed, "Saved Query 改 SQL");
        Require(await repository.EditSavedQuerySqlAsync(edit, token).ConfigureAwait(false) == SavedQueryWriteResult.Conflict, "Saved Query 改 SQL 過期版本保護");
        var edited = await repository.ReadSavedQueryAsync(savedId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Saved Query 編輯後遺失。");
        Require(edited.Query.CurrentRevisionId == edit.RevisionId && edited.ContentId == QueryContent.Create(first).ContentId, "Saved Query 換到新版本");
        Require(edited.Query with { CurrentRevisionId = before.Query.CurrentRevisionId } == before.Query, "改 SQL 不動名稱、scope 或 Pinned");
        Require((await repository.ReadHistoryAsync(new QueryHistoryRequest(50, QueryHistoryKind.All, first), token)
            .ConfigureAwait(false)).Items.Count == 0, "Saved Query 編輯不進 History");
        var again = new SavedQueryEdit(savedId, edited.Version, Guid.NewGuid(), second, start.AddSeconds(200));
        Require(await repository.EditSavedQuerySqlAsync(again, token).ConfigureAwait(false) == SavedQueryWriteResult.Committed, "Saved Query 再次改 SQL");
        // 每 Saved 版本配額只留最新一版；目前版本與擷取產生的版本都不受影響。
        await DrainAsync(repository, new QueryMemoryMaintenancePolicy(null, null, null, null, null, 1), token).ConfigureAwait(false);
        Require(await repository.ReadContentAsync(QueryContent.Create(first).ContentId, token).ConfigureAwait(false) == null, "配額回收舊版本");
        var kept = await repository.ReadSavedQueryAsync(savedId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("配額後 Saved Query 遺失。");
        Require(kept.Query.CurrentRevisionId == again.RevisionId && kept.ContentId == QueryContent.Create(second).ContentId, "配額保留目前版本");
        return kept.Version;
    }

    private static async Task VerifyMaintenanceAsync(IsolatedQueryMemoryRepository repository, string contentId, string sql, CancellationToken token)
    {
        var usage = await repository.ReadUsageAsync(token).ConfigureAwait(false);
        Require(usage.ContentBytes == 2L * (sql.Length + SavedEditSql.Length) && usage.DatabaseFileBytes > 0, "邏輯容量與實體檔案量測");
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

    private static async Task<QueryMemoryMaintenanceResult> DrainAsync(IsolatedQueryMemoryRepository repository,
        QueryMemoryMaintenancePolicy policy, CancellationToken token)
    {
        string? cursor = null;
        for (var batch = 0; batch < 40; batch++)
        {
            token.ThrowIfCancellationRequested();
            var result = await repository.MaintainAsync(new QueryMemoryMaintenanceRequest(policy, 4, cursor), token).ConfigureAwait(false);
            Require(result.ExaminedCandidates <= 4 && result.DeletedRows <= 8, "清理工作量上限");
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
