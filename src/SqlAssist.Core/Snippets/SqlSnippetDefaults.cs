using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Snippets;

/// <summary>內建 Snippet 的唯一出處。</summary>
public static class SqlSnippetDefaults
{
    private const string ResourceName = "SqlAssist.Core.Snippets.DefaultSnippets.json";

    /// <summary>依語言各載入一份：標題、說明與欄位提示疊上該語言的覆蓋檔。</summary>
    private static readonly SqlLanguageCache<SqlSnippetLibrary> Libraries = new(Load);

    /// <summary>隨組件發布、可由新版 VSIX 更新的 49 筆內建定義，文字是目前的介面語言。</summary>
    public static SqlSnippetLibrary Current => Libraries.Current;

    /// <summary>指定語言的內建定義。</summary>
    public static SqlSnippetLibrary For(SqlLanguage language) => Libraries.For(language);

    /// <summary>這一筆是否與某個語言的內建定義完全相同（同編號、同內容）。</summary>
    /// <remarks>
    /// 管理介面在語言 A 載入、換到語言 B 才存檔時，沒改過的內建項目仍是 A 的文字；只和目前語言比
    /// 會把它們全部當成「已自訂」寫進使用者檔，之後就再也跟不上新版內建值。
    /// </remarks>
    public static bool IsUnmodifiedBuiltIn(SqlSnippet snippet)
    {
        if (snippet is null)
        {
            throw new ArgumentNullException(nameof(snippet));
        }

        return SqlLanguage.All.Any(language =>
            For(language).TryGetById(snippet.Id, out var definition) &&
            SqlSnippetMerger.AreEquivalent(snippet, definition));
    }

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
    /// 沒有內建片段只是少了 49 筆建議，其餘功能照常。
    /// </remarks>
    private static SqlSnippetLibrary Load(SqlLanguage language)
    {
        try
        {
            var assembly = typeof(SqlSnippetDefaults).GetTypeInfo().Assembly;

            using var stream = assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return Fail(SnippetText.DefaultsMissing(ResourceName));
            }

            using var reader = new StreamReader(stream);
            var document = SqlSnippetSerializer.DeserializeDocument(reader.ReadToEnd());

            if (document.Version != SqlSnippetLibrary.CurrentVersion)
            {
                return Fail(
                    SnippetText.DefaultsVersionMismatch(document.Version, SqlSnippetLibrary.CurrentVersion));
            }

            var overlay = SqlTextOverlay.Load(assembly, ResourceName, language);
            var snippets = new List<SqlSnippet>(document.Snippets.Count);

            foreach (var record in document.Snippets)
            {
                if (!record.Disabled && record.Snippet is { } snippet)
                {
                    snippets.Add(Localize(snippet, overlay));
                }
            }

            return snippets.Count == 0
                ? Fail(SnippetText.DefaultsEmpty)
                : new SqlSnippetLibrary(snippets);
        }
        catch (Exception exception)
        {
            return Fail(SnippetText.DefaultsReadFailed(exception.Message));
        }
    }

    /// <summary>疊上覆蓋檔；編號是片段的 <c>id</c>。</summary>
    /// <remarks>
    /// 欄位是 <c>title</c>、<c>description</c>、<c>code</c>（只翻註解）與
    /// <c>placeholders.&lt;欄位 id&gt;.tooltip</c>／<c>.default</c>；其餘設定不隨語言變。
    /// </remarks>
    private static SqlSnippet Localize(SqlSnippet snippet, SqlTextOverlay overlay)
    {
        var id = snippet.Id;

        if (overlay.FieldsOf(id).Count == 0)
        {
            return snippet;
        }

        var placeholders = snippet.Placeholders
            .Select(placeholder => new SqlSnippetPlaceholder(
                placeholder.Id,
                overlay.Apply(id, "placeholders." + placeholder.Id + ".default", placeholder.DefaultValue),
                overlay.Apply(id, "placeholders." + placeholder.Id + ".tooltip", placeholder.ToolTip)))
            .ToArray();

        return new SqlSnippet(
            snippet.Shortcut,
            overlay.Apply(id, "code", snippet.Code),
            overlay.Apply(id, "title", snippet.Title),
            overlay.Apply(id, "description", snippet.Description),
            snippet.TriggerFollowUp,
            placeholders,
            id,
            snippet.Category,
            snippet.IsDestructive,
            snippet.ExpansionMode,
            snippet.Positions);
    }

    private static SqlSnippetLibrary Fail(string reason)
    {
        LastError = reason;
        return SqlSnippetLibrary.Empty;
    }
}
