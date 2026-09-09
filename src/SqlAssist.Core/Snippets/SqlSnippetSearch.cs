using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Core.Snippets;

/// <summary>片段搜尋：多詞跨捷徑、名稱與說明比對，不更動來源順序。</summary>
public static class SqlSnippetSearch
{
    public static IReadOnlyList<SqlSnippet> Filter(IReadOnlyList<SqlSnippet> snippets, string? query)
    {
        var terms = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return snippets;
        }

        return snippets.Where(snippet => terms.All(term =>
            snippet.Shortcut.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            snippet.Title.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
            snippet.Description.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
    }
}
