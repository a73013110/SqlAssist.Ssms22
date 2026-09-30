using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 跨夜保留的分類：每一群是 gap（確認要補）、ignore（不補）或 fixed（修好了），附理由。
/// </summary>
/// <remarks>
/// 由晨間審查寫（見 docs/completion-audit.md），工具只讀：報告只列沒分類過的新群，與標成 fixed 卻又出現的退化。
/// 不存在時建一份空的，讓審查的人知道要寫在哪裡。
/// </remarks>
internal sealed class AuditState
{
    public const string Gap = "gap";

    public const string Ignore = "ignore";

    public const string Fixed = "fixed";

    private const string Template = "{\n  \"clusters\": {\n  }\n}\n";

    private AuditState(Dictionary<string, Classification> clusters)
    {
        Clusters = clusters;
    }

    public IReadOnlyDictionary<string, Classification> Clusters { get; }

    public static AuditState Load(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Template, new UTF8Encoding(false));
        }

        var clusters = new Dictionary<string, Classification>(StringComparer.Ordinal);

        foreach (var pair in AuditJson.Parse(File.ReadAllText(path)).Object("clusters"))
        {
            var node = pair.Value as Dictionary<string, object>
                ?? throw new InvalidDataException($"分類檔 {path} 的 {pair.Key} 不是物件。");
            var status = node.String("status");

            if (status is not (Gap or Ignore or Fixed))
            {
                throw new InvalidDataException($"分類檔 {path} 的 {pair.Key} 狀態是「{status}」，只收 gap、ignore、fixed。");
            }

            clusters[pair.Key] = new Classification(status, node.String("reason"));
        }

        return new AuditState(clusters);
    }

    public sealed class Classification
    {
        public Classification(string status, string? reason)
        {
            Status = status;
            Reason = reason;
        }

        public string Status { get; }

        public string? Reason { get; }
    }
}
