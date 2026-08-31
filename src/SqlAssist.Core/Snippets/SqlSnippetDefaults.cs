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

    /// <summary>上一次載入內建資源失敗的原因；成功時為 null。</summary>
    /// <remarks>
    /// 呼叫端（Ssms22 的 Snippet 檔存取）拿它去寫診斷紀錄與管理介面的錯誤列。
    /// </remarks>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 讀內嵌資源。
    /// </summary>
    /// <remarks>
    /// 讀不到一律降級成空清單，<b>不</b>丟例外。這是建置期的錯（資源沒有進組件、
    /// 內容壞掉），正確性由 <c>SqlSnippetDefaultsTests</c> 守；而執行期這個屬性掛在
    /// 建議清單的路徑上，丟出去就是使用者每按一次鍵看到一次錯誤對話框，而且
    /// <see cref="Lazy{T}"/> 會把例外<b>永久快取</b>起來反覆重丟。
    /// 沒有內建片段只是少了 40 筆建議，其餘功能照常。
    /// </remarks>
    private static SqlSnippetLibrary LoadCurrent()
    {
        try
        {
            var assembly = typeof(SqlSnippetDefaults).GetTypeInfo().Assembly;

            using var stream = assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return Fail($"找不到內建 Snippet 資源：{ResourceName}");
            }

            using var reader = new StreamReader(stream);
            var document = SqlSnippetSerializer.DeserializeDocument(reader.ReadToEnd());

            if (document.Version != SqlSnippetLibrary.CurrentVersion)
            {
                return Fail(
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

            return snippets.Count == 0
                ? Fail("內建 Snippet 資源沒有可用項目。")
                : new SqlSnippetLibrary(snippets);
        }
        catch (Exception exception)
        {
            return Fail($"內建 Snippet 資源讀取失敗：{exception.Message}");
        }
    }

    private static SqlSnippetLibrary Fail(string reason)
    {
        LastError = reason;
        return SqlSnippetLibrary.Empty;
    }
}
