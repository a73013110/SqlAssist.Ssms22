using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 一整份「這一輪生效」的 Snippet 清單。
/// </summary>
/// <remarks>
/// 不可變而且<b>唯讀</b>：異動一律走
/// <see cref="SqlSnippetMerger"/>（內建值＋使用者紀錄合併出新的一份）。
/// 這裡刻意不提供 Set／Remove，因為以捷徑為鍵的異動在有了穩定 ID 之後語意是壞的
/// ——換掉一筆同捷徑但不同 ID 的片段，會安靜地把對方的 ID 也一起換掉。
///
/// 讀取端（建議清單、展開器）拿到的永遠是一致的一份，而且是穩定的參考：
/// <c>SqlAsyncCompletionSource</c> 靠比對參考決定要不要重建整批候選項。
/// </remarks>
public sealed class SqlSnippetLibrary
{
    /// <summary>檔案格式版本；欄位語意有不相容變動時才加。</summary>
    public const int CurrentVersion = 2;

    private readonly Dictionary<string, SqlSnippet> _byShortcut;
    private readonly Dictionary<string, SqlSnippet> _byId;

    public SqlSnippetLibrary(IReadOnlyList<SqlSnippet> snippets)
    {
        Snippets = snippets ?? Array.Empty<SqlSnippet>();
        _byShortcut = new Dictionary<string, SqlSnippet>(StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<string, SqlSnippet>(StringComparer.OrdinalIgnoreCase);

        foreach (var snippet in Snippets)
        {
            // 重複的捷徑以第一筆為準。檔案是使用者可以手改的，
            // 撞名時安靜地擇一遠比整份讀取失敗好。
            if (!_byShortcut.ContainsKey(snippet.Shortcut))
            {
                _byShortcut[snippet.Shortcut] = snippet;
            }

            if (!string.IsNullOrWhiteSpace(snippet.Id) && !_byId.ContainsKey(snippet.Id))
            {
                _byId[snippet.Id] = snippet;
            }
        }
    }

    public static SqlSnippetLibrary Empty { get; } = new(Array.Empty<SqlSnippet>());

    public IReadOnlyList<SqlSnippet> Snippets { get; }

    public int Count => Snippets.Count;

    public bool TryGet(string shortcut, out SqlSnippet snippet)
    {
        if (string.IsNullOrEmpty(shortcut))
        {
            snippet = null!;
            return false;
        }

        return _byShortcut.TryGetValue(shortcut, out snippet!);
    }

    public bool TryGetById(string id, out SqlSnippet snippet)
    {
        if (string.IsNullOrEmpty(id))
        {
            snippet = null!;
            return false;
        }

        return _byId.TryGetValue(id, out snippet!);
    }

    /// <summary>
    /// 檢查一個捷徑能不能用。
    /// </summary>
    /// <param name="shortcut">要檢查的捷徑。</param>
    /// <param name="allowedExisting">
    /// 編輯既有項目時傳入它原本的捷徑，否則「沒有改到捷徑」會被自己擋下來。
    /// </param>
    /// <param name="error">不能用時的原因。</param>
    public bool ValidateShortcut(string? shortcut, string? allowedExisting, out string error)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            error = "捷徑不能空白。";
            return false;
        }

        // 展開器是在「游標前方的那一個詞元」上比對的，含空白或標點的捷徑
        // 永遠不會被切成同一個詞元，也就永遠展不開。與其存進去再讓使用者
        // 納悶為什麼沒反應，不如當場擋下來。
        foreach (var character in shortcut!)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                error = $"捷徑只能用字母、數字與底線，不能有「{character}」。";
                return false;
            }
        }

        if (!string.Equals(shortcut, allowedExisting, StringComparison.OrdinalIgnoreCase) &&
            _byShortcut.ContainsKey(shortcut))
        {
            error = $"捷徑「{shortcut}」已經有人用了。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
