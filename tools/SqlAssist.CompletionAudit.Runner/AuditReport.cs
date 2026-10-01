using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 一輪的輸出：<c>clusters.md</c> 給 AI 審查（最多三十群、名稱是代號），<c>raw.jsonl</c> 是完整紀錄（不給 AI 讀）。
/// </summary>
internal static class AuditReport
{
    public const int MaximumClusters = 30;

    public const string ClustersFile = "clusters.md";

    public const string RawFile = "raw.jsonl";

    /// <summary>寫出這一輪的報告，回傳一行總結。</summary>
    public static string Write(
        string folder,
        AuditRunSummary summary,
        IReadOnlyList<AuditResult> results,
        AuditState state,
        SaltedMask mask,
        bool rawNames)
    {
        Directory.CreateDirectory(folder);
        var clusters = results
            .SelectMany(result => result.Misses)
            .GroupBy(miss => miss.Signature.Id)
            .Select(group => new Cluster(group.Key, group.ToList(), state))
            .ToList();

        summary.Clusters = clusters.Count;
        summary.NewClusters = clusters.Count(cluster => cluster.Status == ClusterStatus.New);
        summary.Regressions = clusters.Count(cluster => cluster.Status == ClusterStatus.Regression);
        summary.KnownGaps = clusters.Count(cluster => cluster.Status == ClusterStatus.Gap);
        summary.Ignored = clusters.Count(cluster => cluster.Status == ClusterStatus.Ignore);
        var line = summary.ToLine();

        WriteClusters(Path.Combine(folder, ClustersFile), line, clusters, rawNames);
        WriteRaw(Path.Combine(folder, RawFile), line, summary, results, clusters, mask);
        return line;
    }

    private static void WriteClusters(string path, string summary, IReadOnlyList<Cluster> clusters, bool rawNames)
    {
        var shown = clusters
            .Where(cluster => cluster.Status is ClusterStatus.New or ClusterStatus.Regression)
            .OrderBy(cluster => cluster.Status == ClusterStatus.Regression ? 0 : 1)
            .ThenByDescending(cluster => cluster.Misses.Count)
            .ThenBy(cluster => cluster.Id, StringComparer.Ordinal)
            .ToList();
        var listed = shown.Take(MaximumClusters).ToList();

        var builder = new StringBuilder();
        builder.Append(summary).Append("\n\n");
        builder.Append("只列新群與退化（state.json 標 fixed 卻又出現），依次數排序；分類寫進 state.json。\n");

        foreach (var status in new[] { ClusterStatus.Regression, ClusterStatus.New })
        {
            var section = listed.Where(cluster => cluster.Status == status).ToList();

            if (section.Count == 0)
            {
                continue;
            }

            builder.Append("\n## ").Append(status == ClusterStatus.Regression ? "退化" : "新群").Append('\n');

            foreach (var cluster in section)
            {
                var example = cluster.Representative;
                builder.Append("\n### ").Append(cluster.Id)
                    .Append(" · ").Append(cluster.Misses.Count).Append(" 次")
                    .Append(" · ").Append(cluster.SsmsText)
                    .Append(" · ").Append(ClassText(example.TokenClass))
                    .Append(" · ").Append(KindText(example.Kind)).Append('\n');
                builder.Append('`').Append(cluster.Signature).Append("`\n");
                builder.Append("```sql\n").Append(rawNames ? example.Example : example.MaskedExample).Append("\n```\n");
            }
        }

        var hidden = shown.Count - listed.Count;

        if (hidden > 0)
        {
            builder.Append("\n另有 ").Append(hidden).Append(" 群未列（上限 ").Append(MaximumClusters).Append("），完整在 raw.jsonl。\n");
        }

        if (shown.Count == 0)
        {
            builder.Append("\n沒有新群，也沒有退化。\n");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteRaw(
        string path,
        string summaryLine,
        AuditRunSummary summary,
        IReadOnlyList<AuditResult> results,
        IReadOnlyList<Cluster> clusters,
        SaltedMask mask)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(AuditJson.Serialize(new { type = "run", summary = summaryLine, detail = summary }));

        foreach (var pair in mask.Codes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteLine(AuditJson.Serialize(new { type = "mask", code = pair.Key, name = pair.Value }));
        }

        var status = clusters.ToDictionary(cluster => cluster.Id, cluster => cluster.Status, StringComparer.Ordinal);

        foreach (var fragment in results.SelectMany(result => result.Misses).Select(miss => miss.Fragment).Distinct())
        {
            writer.WriteLine(AuditJson.Serialize(new
            {
                type = "fragment",
                source = fragment.Source,
                id = fragment.Id,
                database = fragment.Database,
                wordsInUpperCase = fragment.WordsInUpperCase,
                text = fragment.Text,
            }));
        }

        foreach (var miss in results.SelectMany(result => result.Misses))
        {
            writer.WriteLine(AuditJson.Serialize(new
            {
                type = "miss",
                fragment = miss.Fragment.Id,
                cluster = miss.Signature.Id,
                status = status[miss.Signature.Id].ToString(),
                record = MissRecord.From(miss).Write(),
            }));
        }
    }

    public static string ClassText(AuditTokenClass tokenClass) => tokenClass switch
    {
        AuditTokenClass.Word => "字",
        AuditTokenClass.GlobalVariable => "全域變數",
        AuditTokenClass.ScriptName => "指令碼取的名稱",
        AuditTokenClass.Object => "物件",
        AuditTokenClass.Schema => "結構描述",
        AuditTokenClass.Database => "資料庫",
        AuditTokenClass.Column => "欄位",
        _ => tokenClass.ToString(),
    };

    public static string KindText(AuditMissKind kind) => kind switch
    {
        AuditMissKind.Closed => "清單不開",
        AuditMissKind.Absent => "不在候選",
        AuditMissKind.Hidden => "候選有、打字後看不到",
        _ => kind.ToString(),
    };

    public enum ClusterStatus
    {
        New,
        Regression,
        Gap,
        Ignore,
    }

    /// <summary>同一個簽章的漏；代表範例取那一句最短的一次。</summary>
    private sealed class Cluster
    {
        public Cluster(string id, List<AuditMiss> misses, AuditState state)
        {
            Id = id;
            Misses = misses;
            Representative = misses.OrderBy(miss => miss.ExampleLength).ThenBy(miss => miss.Fragment.Id, StringComparer.Ordinal).First();
            Signature = Representative.Signature.Text;
            Status = state.Clusters.TryGetValue(id, out var classification)
                ? classification.Status switch
                {
                    AuditState.Fixed => ClusterStatus.Regression,
                    AuditState.Ignore => ClusterStatus.Ignore,
                    _ => ClusterStatus.Gap,
                }
                : ClusterStatus.New;

            var asked = misses.Where(miss => miss.SsmsLists is not null).ToList();
            SsmsText = asked.Count == 0
                ? "SSMS 未問"
                : $"SSMS 列出 {asked.Count(miss => miss.SsmsLists == true)}/{asked.Count}";
        }

        public string Id { get; }

        public List<AuditMiss> Misses { get; }

        public AuditMiss Representative { get; }

        public string Signature { get; }

        public ClusterStatus Status { get; }

        public string SsmsText { get; }
    }
}

/// <summary>一輪的數字；報告第一行與原始紀錄的 run 列。</summary>
internal sealed class AuditRunSummary
{
    [System.Web.Script.Serialization.ScriptIgnore]
    public DateTimeOffset StartedAt { get; set; }

    public string Started => StartedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    public double Minutes { get; set; }

    public int Fragments { get; set; }

    public int Audited { get; set; }

    public int Cached { get; set; }

    public int Unfinished { get; set; }

    public int TooLong { get; set; }

    public int Tokens { get; set; }

    public int Excluded { get; set; }

    public int UnparsedStatements { get; set; }

    public int Misses { get; set; }

    public int Clusters { get; set; }

    public int NewClusters { get; set; }

    public int Regressions { get; set; }

    public int KnownGaps { get; set; }

    public int Ignored { get; set; }

    public int SsmsAsked { get; set; }

    public int SsmsListed { get; set; }

    public int SsmsTimeouts { get; set; }

    public List<string> Failures { get; set; } = new();

    public string ToLine()
    {
        var line = new StringBuilder()
            .Append(StartedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))
            .Append("｜語料 ").Append(Fragments).Append(" 段（重跑 ").Append(Audited).Append("、快取 ").Append(Cached);

        if (Unfinished > 0)
        {
            line.Append("、未完 ").Append(Unfinished);
        }

        if (TooLong > 0)
        {
            line.Append("、過長略過 ").Append(TooLong);
        }

        line.Append("）｜稽核 ").Append(Tokens).Append(" 詞、排除 ").Append(Excluded)
            .Append("（剖析失敗 ").Append(UnparsedStatements).Append(" 句）")
            .Append("｜漏 ").Append(Misses).Append(" 次 / ").Append(Clusters).Append(" 群")
            .Append("｜新 ").Append(NewClusters).Append("｜退化 ").Append(Regressions)
            .Append("｜已知 gap ").Append(KnownGaps).Append("｜忽略 ").Append(Ignored)
            .Append("｜SSMS 列出 ").Append(SsmsListed).Append('/').Append(SsmsAsked);

        if (SsmsTimeouts > 0)
        {
            line.Append("（逾時 ").Append(SsmsTimeouts).Append('）');
        }

        if (Failures.Count > 0)
        {
            line.Append("｜失敗：").Append(string.Join("、", Failures));
        }

        return line.Append("｜").Append(Minutes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" 分").ToString();
    }
}
