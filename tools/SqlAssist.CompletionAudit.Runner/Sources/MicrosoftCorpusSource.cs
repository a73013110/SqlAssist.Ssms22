using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.CompletionAudit.Runner.Sources;

/// <summary>
/// 微軟公開的 T-SQL：SqlScriptDOM 的剖析器測試腳本與 sql-docs 的範例，釘在 <c>corpora.json</c> 的 commit。
/// </summary>
/// <remarks>
/// 第一次用到時下載到 <c>artifacts/completion-audit/corpora/&lt;名稱&gt;/&lt;commit&gt;/</c>，旁邊寫一份
/// <c>manifest.json</c> 記來源、commit 與授權；之後只讀快取。不進版控：授權允許引用，但語料不是本專案的程式。
/// 換語料就改 commit，舊的那一份留著也不會再被讀。
///
/// 沒有資料庫可配，名稱查不到存在、不稽核；這一份的價值在字：新版語法與少見子句的覆蓋。
/// </remarks>
internal sealed class MicrosoftCorpusSource : IAuditCorpusSource
{
    public const string SourceName = "microsoft";

    /// <summary>sql-docs 只取可以執行的範例；<c>syntaxsql</c> 是語法圖，不是 SQL。</summary>
    private static readonly Regex SqlFence = new(
        @"^[ \t]*```[ \t]*(sql|tsql|t-sql)[ \t]*\r?\n(?<code>.*?)^[ \t]*```",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _configPath;
    private readonly string _cacheRoot;
    private readonly Action<string> _log;

    public MicrosoftCorpusSource(string configPath, string cacheRoot, Action<string> log)
    {
        _configPath = configPath;
        _cacheRoot = cacheRoot;
        _log = log;
    }

    public string Name => SourceName;

    /// <summary>下載失敗的語料；報告的總結列寫出來。</summary>
    public List<string> Failures { get; } = new();

    public IEnumerable<AuditFragment> Read(CancellationToken cancellationToken)
    {
        var fragments = new List<AuditFragment>();

        foreach (var corpus in Corpus.Load(_configPath))
        {
            var folder = Path.Combine(_cacheRoot, corpus.Name, corpus.Commit);

            if (!File.Exists(Path.Combine(folder, "manifest.json")))
            {
                try
                {
                    DownloadAsync(corpus, folder, cancellationToken).GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or FormatException or InvalidOperationException or TaskCanceledException
                    && !cancellationToken.IsCancellationRequested)
                {
                    Failures.Add(corpus.Name);
                    _log($"{corpus.Name}：下載失敗（{exception.Message}），這一次略過。");
                    continue;
                }
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*" + corpus.Extension, SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = file.Substring(folder.Length + 1).Replace('\\', '/');
                var text = File.ReadAllText(file);

                if (corpus.Format == "markdown")
                {
                    var number = 0;

                    foreach (Match block in SqlFence.Matches(text))
                    {
                        number++;
                        fragments.Add(new AuditFragment(SourceName, $"{corpus.Name}:{relative}#{number}", block.Groups["code"].Value));
                    }
                }
                else
                {
                    fragments.Add(new AuditFragment(SourceName, $"{corpus.Name}:{relative}", text));
                }
            }
        }

        return fragments;
    }

    /// <summary>沿路徑一段一段取樹（整個 sql-docs 的遞迴樹會被截斷），再逐檔下載原文。</summary>
    private async Task DownloadAsync(Corpus corpus, string folder, CancellationToken cancellationToken)
    {
        _log($"{corpus.Name}：下載 {corpus.Repository}@{corpus.Commit.Substring(0, 8)} 的 {corpus.Path}…");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SqlAssist-CompletionAudit");

        var tree = $"https://api.github.com/repos/{corpus.Repository}/git/trees/{corpus.Commit}";

        foreach (var segment in corpus.Path.Split('/'))
        {
            var listing = AuditJson.Parse(await client.GetStringAsync(tree).ConfigureAwait(false));
            tree = Entries(listing)
                .First(entry => entry.String("path") == segment && entry.String("type") == "tree")
                .RequiredString("url");
        }

        var all = AuditJson.Parse(await client.GetStringAsync(tree + "?recursive=1").ConfigureAwait(false));

        if (all.Bool("truncated"))
        {
            throw new InvalidOperationException($"{corpus.Path} 的檔案樹太大被截斷，改指到更深的資料夾。");
        }

        var files = Entries(all)
            .Where(entry => entry.String("type") == "blob")
            .Select(entry => entry.RequiredString("path"))
            .Where(path => path.EndsWith(corpus.Extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var staging = folder + ".partial";
        Directory.CreateDirectory(staging);

        using var throttle = new SemaphoreSlim(8);
        await Task.WhenAll(files.Select(async path =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var target = Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(target))
                {
                    return;
                }

                var url = $"https://raw.githubusercontent.com/{corpus.Repository}/{corpus.Commit}/{corpus.Path}/{path}";
                var text = await client.GetStringAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text, new UTF8Encoding(false));
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        File.WriteAllText(
            Path.Combine(staging, "manifest.json"),
            AuditJson.Serialize(new
            {
                repository = corpus.Repository,
                commit = corpus.Commit,
                path = corpus.Path,
                license = corpus.License,
                licenseUrl = corpus.LicenseUrl,
                files = files.Length,
                downloadedAt = DateTimeOffset.Now.ToString("o"),
            }),
            new UTF8Encoding(false));

        // 下載到一半中斷時留的是 .partial，下一次接著下；只有完整的才改名成正式的資料夾。
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.Move(staging, folder);
        _log($"{corpus.Name}：{files.Length} 個檔案（{corpus.License}）。");
    }

    private static IEnumerable<Dictionary<string, object>> Entries(Dictionary<string, object> listing) =>
        listing.Array("tree").OfType<Dictionary<string, object>>();

    private sealed class Corpus
    {
        public string Name { get; private set; } = string.Empty;

        public string Repository { get; private set; } = string.Empty;

        public string Commit { get; private set; } = string.Empty;

        public string Path { get; private set; } = string.Empty;

        public string Extension { get; private set; } = string.Empty;

        public string Format { get; private set; } = string.Empty;

        public string License { get; private set; } = string.Empty;

        public string LicenseUrl { get; private set; } = string.Empty;

        public static IReadOnlyList<Corpus> Load(string path)
        {
            return AuditJson.Parse(File.ReadAllText(path)).Array("corpora").OfType<Dictionary<string, object>>().Select(item => new Corpus
            {
                Name = item.RequiredString("name"),
                Repository = item.RequiredString("repository"),
                Commit = item.RequiredString("commit"),
                Path = item.RequiredString("path"),
                Extension = item.RequiredString("extension"),
                Format = item.RequiredString("format"),
                License = item.RequiredString("license"),
                LicenseUrl = item.RequiredString("licenseUrl"),
            }).ToArray();
        }
    }
}
