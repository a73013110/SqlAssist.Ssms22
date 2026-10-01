using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.CompletionAudit.Runner.Sources;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using SqlAssist.Metadata.Completion;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 一輪稽核：讀語料、跳過快取命中的段、在時間上限內平行跑其餘的、只在漏的位置問 SSMS，最後寫報告。
/// </summary>
internal sealed class AuditRun
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMinutes(1);

    private readonly AuditOptions _options;
    private readonly Dictionary<string, Database> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly AuditRunSummary _summary = new() { StartedAt = DateTimeOffset.Now };
    private AuditConnection? _connection;
    private SaltedMask _mask = null!;
    private CompletionAuditor _auditor = null!;

    public AuditRun(AuditOptions options)
    {
        _options = options;
    }

    private static string MaskKeyPath => Path.Combine(Path.GetDirectoryName(AuditConnection.DefaultPath)!, "mask.key");

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        _connection = AuditConnection.TryLoad(_options.ConnectionPath);
        _mask = SaltedMask.Load(MaskKeyPath);
        _auditor = new CompletionAuditor(new SqlAssistSettings(), BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current), _mask);
        var state = AuditState.Load(Path.Combine(_options.OutputRoot, "state.json"));

        await ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (_options.Cluster is { } cluster)
        {
            return await RecheckAsync(cluster, cancellationToken).ConfigureAwait(false);
        }

        var fragments = Read(cancellationToken);
        var version = Version();
        var cache = AuditCache.Load(Path.Combine(_options.OutputRoot, "cache", "results.jsonl"), _options.NoCache);
        var results = new ConcurrentBag<AuditResult>();
        var keys = new List<string>();
        var work = new ConcurrentQueue<(AuditFragment Fragment, string Key)>();

        foreach (var fragment in fragments)
        {
            if (fragment.Text.Length > _options.MaxFragmentChars)
            {
                _summary.TooLong++;
                continue;
            }

            var key = AuditHash.Of($"{version}\n{DatabaseKey(fragment)}\n{fragment.WordsInUpperCase}\n{fragment.Text}");
            keys.Add(key);

            if (cache.TryGet(key, fragment, out var cached))
            {
                results.Add(cached);
                _summary.Cached++;
            }
            else
            {
                work.Enqueue((fragment, key));
            }
        }

        Log($"語料 {_summary.Fragments} 段：快取 {_summary.Cached}、要跑 {work.Count}、過長略過 {_summary.TooLong}；平行 {_options.Parallel}、上限 {_options.MaxMinutes} 分。");
        var interrupted = await AuditAllAsync(work, cache, results, cancellationToken).ConfigureAwait(false);
        cache.Flush();
        _summary.Unfinished = work.Count + interrupted;

        if (_summary.Unfinished == 0 && !cancellationToken.IsCancellationRequested)
        {
            cache.Compact(keys);
        }

        var all = results.ToList();
        var tally = new AuditTally();

        foreach (var result in all)
        {
            tally.Add(result.Tally);
        }

        _summary.Tokens = tally.Audited.Values.Sum();
        _summary.Excluded = tally.Excluded.Values.Sum();
        _summary.UnparsedStatements = tally.UnparsedStatements;
        _summary.Misses = all.Sum(result => result.Misses.Count);
        var asked = all.SelectMany(result => result.Misses).Where(miss => miss.SsmsLists is not null).ToList();
        _summary.SsmsAsked = asked.Count;
        _summary.SsmsListed = asked.Count(miss => miss.SsmsLists == true);
        _summary.SsmsTimeouts = _databases.Values.Sum(database => database.Opinion?.Timeouts ?? 0);
        _summary.Minutes = timer.Elapsed.TotalMinutes;

        var stamp = _summary.StartedAt.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var folder = Path.Combine(_options.OutputRoot, stamp);

        // 同一分鐘裡跑第二次時不蓋掉前一份。
        for (var suffix = 2; Directory.Exists(folder); suffix++)
        {
            folder = Path.Combine(_options.OutputRoot, $"{stamp}-{suffix}");
        }
        var line = AuditReport.Write(folder, _summary, all, state, _mask, _options.RawNames);
        Console.WriteLine(line);
        Console.WriteLine($"報告：{Path.Combine(folder, AuditReport.ClustersFile)}");

        foreach (var database in _databases.Values)
        {
            database.Opinion?.Dispose();
        }

        // 有漏不算失敗；一段都沒跑成（來源全掛、全部例外）才是工具的問題。
        return _summary.Fragments > 0 && _summary.Audited + _summary.Cached == 0 ? 1 : 0;
    }

    /// <summary>連線設定裡要用的資料庫各建一份目錄；連不上的記下來，那個資料庫的語料改用沒有中繼資料的判定。</summary>
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            Log($"沒有連線設定（{_options.ConnectionPath}）：只跑不需要資料庫的語料，名稱只稽核指令碼自己取的。");
            return;
        }

        foreach (var name in _options.Databases ?? _connection.Databases)
        {
            try
            {
                var catalog = new DatabaseAuditCatalog(_connection, name);
                var signature = await catalog.SignatureAsync(cancellationToken).ConfigureAwait(false);
                _databases[name] = new Database(catalog, signature, new SsmsOpinion(_connection, name));
                Log($"資料庫 {name}：已連線（{_connection.Describe()}）。");
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
            {
                _summary.Failures.Add($"連線 {name}");
                Log($"資料庫 {name}：連不上（{exception.Message}）。");
            }
        }
    }

    private List<AuditFragment> Read(CancellationToken cancellationToken)
    {
        var sources = new List<IAuditCorpusSource>();

        if (_options.Uses(RecallCorpusSource.SourceName))
        {
            sources.Add(new RecallCorpusSource(Path.Combine(_options.Repository, "tests", "SqlAssist.Core.Tests", "Keywords", "RecallCorpus.sql")));
        }

        if (_options.Uses(SqlMemorySource.SourceName))
        {
            sources.Add(new SqlMemorySource(_options.Ide, _databases.Keys.ToArray(), Log));
        }

        var modules = _connection is not null && _options.Uses(ModuleSource.SourceName)
            ? new ModuleSource(_connection, _databases.Keys.ToArray(), Log)
            : null;

        if (modules is not null)
        {
            sources.Add(modules);
        }
        else if (_options.Uses(ModuleSource.SourceName))
        {
            Log("模組定義：沒有連線設定，略過。");
        }

        var microsoft = _options.Uses(MicrosoftCorpusSource.SourceName)
            ? new MicrosoftCorpusSource(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "corpora.json"),
                Path.Combine(_options.OutputRoot, "corpora"),
                Log)
            : null;

        if (microsoft is not null)
        {
            sources.Add(microsoft);
        }

        var fragments = new List<AuditFragment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var count = 0;

            foreach (var fragment in source.Read(cancellationToken))
            {
                // 同一份內容在歷史與模組裡各出現一次時只跑一次。
                if (seen.Add(AuditHash.Of(DatabaseKey(fragment) + "\n" + fragment.Text)))
                {
                    fragments.Add(fragment);
                    count++;
                }
            }

            Log($"{source.Name}：{count} 段。");
        }

        _summary.Failures.AddRange((modules?.Failures ?? new List<string>()).Select(name => $"模組 {name}"));
        _summary.Failures.AddRange((microsoft?.Failures ?? new List<string>()).Select(name => $"下載 {name}"));
        _summary.Fragments = fragments.Count;
        return fragments;
    }

    /// <summary>在時間上限內平行跑；回傳做到一半被停下的段數。</summary>
    private async Task<int> AuditAllAsync(
        ConcurrentQueue<(AuditFragment Fragment, string Key)> work,
        AuditCache cache,
        ConcurrentBag<AuditResult> results,
        CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(_options.MaxMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
        var interrupted = 0;
        var errors = 0;
        var audited = 0;
        var progress = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, _options.Parallel).Select(_ => Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested && work.TryDequeue(out var item))
            {
                try
                {
                    var result = await AuditAsync(item.Fragment, linked.Token).ConfigureAwait(false);
                    cache.Add(item.Key, result);
                    results.Add(result);
                    Interlocked.Increment(ref audited);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    Interlocked.Increment(ref interrupted);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 產品的分析在某段語料上擲出例外，本身就是一個發現；記下來，繼續跑下一段。
                    if (Interlocked.Increment(ref errors) <= 3)
                    {
                        Log($"{item.Fragment.Id}：{exception}");
                    }
                }

                if (progress.Elapsed >= ProgressInterval)
                {
                    lock (progress)
                    {
                        if (progress.Elapsed >= ProgressInterval)
                        {
                            Log($"已跑 {Volatile.Read(ref audited)} 段，剩 {work.Count} 段。");
                            progress.Restart();
                        }
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        _summary.Audited = audited;

        if (errors > 0)
        {
            _summary.Failures.Add($"分析例外 {errors} 段");
        }

        if (budget.IsCancellationRequested)
        {
            Log($"到了 {_options.MaxMinutes} 分鐘上限，剩下的下一次接著跑。");
        }

        return interrupted;
    }

    private async Task<AuditResult> AuditAsync(AuditFragment fragment, CancellationToken cancellationToken)
    {
        var database = fragment.Database is { } name && _databases.TryGetValue(name, out var found) ? found : null;
        var result = await _auditor
            .AuditAsync(fragment, database?.Catalog ?? AuditCatalog.None, cancellationToken)
            .ConfigureAwait(false);

        // 第二意見只問名稱：Resolver 從來不列關鍵字。
        if (database?.Opinion is { } opinion)
        {
            foreach (var miss in result.Misses.Where(miss => miss.TokenClass != AuditTokenClass.Word))
            {
                miss.SsmsLists = await opinion
                    .ListsAsync(fragment.Text.Substring(0, miss.Offset), miss.Word, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>
    /// 重驗一群：找最近一次記到這一群的原始紀錄，每一筆用目前的程式再問一次。
    /// </summary>
    private async Task<int> RecheckAsync(string cluster, CancellationToken cancellationToken)
    {
        var raw = Directory.Exists(_options.OutputRoot)
            ? Directory.GetDirectories(_options.OutputRoot)
                .Select(folder => Path.Combine(folder, AuditReport.RawFile))
                .Where(File.Exists)
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .FirstOrDefault(path => File.ReadLines(path).Any(line => line.Contains($"\"cluster\":\"{cluster}\"")))
            : null;

        if (raw is null)
        {
            Console.Error.WriteLine($"找不到記過 {cluster} 這一群的原始紀錄。");
            return 2;
        }

        var fragments = new Dictionary<string, AuditFragment>(StringComparer.Ordinal);
        var misses = new List<(string Fragment, MissRecord Record)>();

        foreach (var line in File.ReadLines(raw))
        {
            var root = AuditJson.Parse(line);

            switch (root.String("type"))
            {
                case "fragment":
                    var id = root.RequiredString("id");
                    fragments[id] = new AuditFragment(
                        root.RequiredString("source"),
                        id,
                        root.RequiredString("text"),
                        root.String("database"),
                        root.Bool("wordsInUpperCase"));
                    break;
                case "miss" when root.String("cluster") == cluster:
                    misses.Add((root.RequiredString("fragment"), MissRecord.Read(root.Object("record"))));
                    break;
            }
        }

        var still = 0;

        foreach (var (fragmentId, record) in misses)
        {
            var fragment = fragments[fragmentId];
            var database = fragment.Database is { } name && _databases.TryGetValue(name, out var found) ? found : null;
            var tally = new AuditTally();
            var again = await _auditor
                .RecheckAsync(fragment, record.Offset, database?.Catalog ?? AuditCatalog.None, tally, cancellationToken)
                .ConfigureAwait(false);
            var outcome = again is not null
                ? again.Signature.Id == cluster ? "仍漏" : "換群 " + again.Signature.Id
                : tally.Excluded.Count > 0 ? "排除 " + tally.Excluded.Keys.First() : "已列出";

            if (again is not null && again.Signature.Id == cluster)
            {
                still++;
            }

            Console.WriteLine($"{outcome}｜{(_options.RawNames ? record.Example : record.Masked).Replace("\n", " ")}");
        }

        Console.WriteLine($"{cluster}：仍漏 {still}/{misses.Count}（{Path.GetFileName(Path.GetDirectoryName(raw))}）");
        return 0;
    }

    /// <summary>
    /// 判定的版本：Core、中繼資料層、稽核判定與執行器組件本身的雜湊。任何一份改了，快取整份失效。
    /// </summary>
    private static string Version() => AuditHash.Of(string.Join("\n", new[]
    {
        typeof(SqlCompletionCandidates).Assembly.Location,
        typeof(SqlCatalogCompletionMetadata).Assembly.Location,
        typeof(CompletionAuditor).Assembly.Location,
        typeof(AuditRun).Assembly.Location,
    }.Select(AuditHash.OfFile)));

    /// <summary>快取鍵裡的資料庫那一段：名稱加快照指紋；沒有連上的一律當成沒有資料庫。</summary>
    private string DatabaseKey(AuditFragment fragment) =>
        fragment.Database is { } name && _databases.TryGetValue(name, out var database)
            ? $"{name}|{database.Signature}"
            : string.Empty;

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private sealed class Database
    {
        public Database(DatabaseAuditCatalog catalog, string signature, SsmsOpinion opinion)
        {
            Catalog = catalog;
            Signature = signature;
            Opinion = opinion;
        }

        public DatabaseAuditCatalog Catalog { get; }

        public string Signature { get; }

        public SsmsOpinion? Opinion { get; }
    }
}
