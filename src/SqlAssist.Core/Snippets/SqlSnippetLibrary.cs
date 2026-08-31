using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 一整份 Snippet 清單。
/// </summary>
/// <remarks>
/// 不可變：管理介面的每一次新增、修改、刪除都產生新的一份，
/// 讀取端不必擔心正在列舉時被改掉。
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

    /// <summary>新增或以捷徑為鍵取代一筆，回傳新的清單。</summary>
    public SqlSnippetLibrary Set(SqlSnippet snippet)
    {
        if (snippet is null)
        {
            throw new ArgumentNullException(nameof(snippet));
        }

        var replaced = false;
        var snippets = new List<SqlSnippet>(Snippets.Count + 1);

        foreach (var existing in Snippets)
        {
            if (string.Equals(existing.Shortcut, snippet.Shortcut, StringComparison.OrdinalIgnoreCase))
            {
                snippets.Add(snippet);
                replaced = true;
            }
            else
            {
                snippets.Add(existing);
            }
        }

        if (!replaced)
        {
            snippets.Add(snippet);
        }

        return new SqlSnippetLibrary(snippets);
    }

    /// <summary>依捷徑改名並取代一筆；<paramref name="originalShortcut"/> 不存在時等同新增。</summary>
    public SqlSnippetLibrary Replace(string originalShortcut, SqlSnippet snippet)
    {
        if (string.Equals(originalShortcut, snippet?.Shortcut, StringComparison.OrdinalIgnoreCase))
        {
            return Set(snippet!);
        }

        return Remove(originalShortcut).Set(snippet!);
    }

    public SqlSnippetLibrary Remove(string shortcut)
    {
        var snippets = Snippets
            .Where(item => !string.Equals(item.Shortcut, shortcut, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return snippets.Length == Snippets.Count ? this : new SqlSnippetLibrary(snippets);
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
