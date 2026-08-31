using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SqlAssist.Core.Snippets;

/// <summary>內建 Snippet 的唯一出處。</summary>
public static class SqlSnippetDefaults
{
    private const string ResourceName = "SqlAssist.Core.Snippets.DefaultSnippets.json";

    private static readonly Lazy<SqlSnippetLibrary> CurrentValue = new(LoadCurrent);

    /// <summary>隨組件發布、可由新版 VSIX 更新的 40 筆內建定義。</summary>
    public static SqlSnippetLibrary Current => CurrentValue.Value;

    /// <summary>
    /// v1 遷移的凍結比較基準。這三筆必須永遠維持 0.13 的原值；
    /// 改成新版預設會讓未修改過的使用者檔案被誤判成三筆 override。
    /// </summary>
    public static SqlSnippetLibrary LegacyVersion1 { get; } = new(new[]
    {
        new SqlSnippet(
            "ssf",
            "SELECT * FROM ",
            "SELECT * FROM",
            "SELECT * FROM fragment",
            triggerFollowUp: true),
        new SqlSnippet(
            "ap",
            "ALTER PROCEDURE ",
            "ALTER PROCEDURE",
            "ALTER PROCEDURE fragment",
            triggerFollowUp: true),
        new SqlSnippet(
            "af",
            "ALTER FUNCTION ",
            "ALTER FUNCTION",
            "ALTER FUNCTION fragment",
            triggerFollowUp: true)
    });

    private static SqlSnippetLibrary LoadCurrent()
    {
        var assembly = typeof(SqlSnippetDefaults).GetTypeInfo().Assembly;

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"找不到內建 Snippet 資源：{ResourceName}");
        using var reader = new StreamReader(stream);
        var document = SqlSnippetSerializer.DeserializeDocument(reader.ReadToEnd());

        if (document.Version != SqlSnippetLibrary.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"內建 Snippet 版本為 {document.Version}，程式支援 {SqlSnippetLibrary.CurrentVersion}。");
        }

        var snippets = new List<SqlSnippet>(document.Snippets.Count);

        foreach (var record in document.Snippets)
        {
            if (!record.Disabled && record.Snippet is { } snippet)
            {
                snippets.Add(snippet);
            }
        }

        var library = new SqlSnippetLibrary(snippets);

        if (library.Count == 0)
        {
            throw new InvalidOperationException("內建 Snippet 資源沒有可用項目。");
        }

        return library;
    }
}
