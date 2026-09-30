using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>命令列參數；由 <c>tools/Audit-Completions.ps1</c> 組好傳進來。</summary>
internal sealed class AuditOptions
{
    public static readonly IReadOnlyList<string> AllSources = new[] { "recall", "memory", "modules", "microsoft" };

    public string Repository { get; private set; } = string.Empty;

    public string Ide { get; private set; } = string.Empty;

    public IReadOnlyList<string> Sources { get; private set; } = AllSources;

    /// <summary>要連的資料庫；null 是連線設定裡的全部。</summary>
    public IReadOnlyList<string>? Databases { get; private set; }

    public double MaxMinutes { get; private set; } = 60;

    public string? Cluster { get; private set; }

    public bool RawNames { get; private set; }

    public bool NoCache { get; private set; }

    /// <summary>連線設定檔；預設是 <see cref="AuditConnection.DefaultPath"/>。</summary>
    public string ConnectionPath { get; private set; } = AuditConnection.DefaultPath;

    public int Parallel { get; private set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>超過這個長度的一段不跑：一段的成本約與長度平方成正比。</summary>
    public int MaxFragmentChars { get; private set; } = 100_000;

    public string OutputRoot => Path.Combine(Repository, "artifacts", "completion-audit");

    public static AuditOptions Parse(IReadOnlyList<string> args)
    {
        var options = new AuditOptions();

        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            string Value() => index + 1 < args.Count ? args[++index] : throw new ArgumentException($"{name} 後面少了值。");

            switch (name)
            {
                case "--repo":
                    options.Repository = Path.GetFullPath(Value());
                    break;
                case "--ide":
                    options.Ide = Value();
                    break;
                case "--sources":
                    options.Sources = List(Value());
                    var unknown = options.Sources.Except(AllSources, StringComparer.OrdinalIgnoreCase).ToArray();

                    if (unknown.Length > 0)
                    {
                        throw new ArgumentException($"不認得的語料來源：{string.Join("、", unknown)}（可用：{string.Join("、", AllSources)}）");
                    }

                    break;
                case "--database":
                    options.Databases = List(Value());
                    break;
                case "--max-minutes":
                    options.MaxMinutes = double.Parse(Value(), CultureInfo.InvariantCulture);
                    break;
                case "--cluster":
                    options.Cluster = Value();
                    break;
                case "--raw-names":
                    options.RawNames = true;
                    break;
                case "--connection":
                    options.ConnectionPath = Path.GetFullPath(Value());
                    break;
                case "--no-cache":
                    options.NoCache = true;
                    break;
                case "--parallel":
                    options.Parallel = Math.Max(1, int.Parse(Value(), CultureInfo.InvariantCulture));
                    break;
                case "--max-fragment-chars":
                    options.MaxFragmentChars = int.Parse(Value(), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"不認得的參數：{name}");
            }
        }

        if (options.Repository.Length == 0 || options.Ide.Length == 0)
        {
            throw new ArgumentException("必須指定 --repo 與 --ide。");
        }

        return options;
    }

    public bool Uses(string source) => Sources.Contains(source, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> List(string value) =>
        value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
}
