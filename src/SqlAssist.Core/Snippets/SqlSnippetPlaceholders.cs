using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Snippets;

/// <summary>
/// 從 Snippet 的程式碼裡找出佔位符。
/// </summary>
/// <remarks>
/// 佔位符清單刻意由程式碼推導，而不是讓使用者在管理介面裡另外維護一份：
/// 兩份東西只要能各自編輯就會分岔，而分岔的症狀是「宣告了卻沒被取代」或
/// 「打了 $x$ 卻沒有欄位可以設定預設值」。使用者能改的只有既有佔位符的
/// 預設值與說明。
/// </remarks>
public static class SqlSnippetPlaceholders
{
    /// <summary>
    /// 依出現順序取出程式碼裡的佔位符名稱，重複的只留第一次。
    /// </summary>
    /// <remarks>
    /// 名稱的規則與識別字相同（字母或底線開頭，後接字母、數字、底線）。
    /// <c>$end$</c> 是游標標記不是佔位符，會被排除；
    /// <c>$1,234$</c> 這種不成名稱的內容不視為佔位符，原樣留在程式碼裡。
    /// </remarks>
    public static IReadOnlyList<string> Extract(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < code!.Length)
        {
            var open = code.IndexOf('$', index);

            if (open < 0 || open + 1 >= code.Length)
            {
                break;
            }

            var end = open + 1;

            if (!IsNameStart(code[end]))
            {
                index = open + 1;
                continue;
            }

            end++;

            while (end < code.Length && IsNamePart(code[end]))
            {
                end++;
            }

            if (end >= code.Length || code[end] != '$')
            {
                index = open + 1;
                continue;
            }

            var name = code.Substring(open + 1, end - open - 1);

            if (!string.Equals(name, "end", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "selected", StringComparison.OrdinalIgnoreCase) &&
                seen.Add(name))
            {
                names.Add(name);
            }

            // 從結尾的 $ 之後繼續：$a$$b$ 的第二個佔位符要認得出來。
            index = end + 1;
        }

        return names;
    }

    /// <summary>
    /// 依程式碼重算佔位符清單，並保留使用者已經設定的預設值與說明。
    /// </summary>
    public static IReadOnlyList<SqlSnippetPlaceholder> Reconcile(
        string? code,
        IReadOnlyList<SqlSnippetPlaceholder>? existing)
    {
        var names = Extract(code);

        if (names.Count == 0)
        {
            return Array.Empty<SqlSnippetPlaceholder>();
        }

        var byId = new Dictionary<string, SqlSnippetPlaceholder>(StringComparer.OrdinalIgnoreCase);

        foreach (var placeholder in existing ?? Array.Empty<SqlSnippetPlaceholder>())
        {
            byId[placeholder.Id] = placeholder;
        }

        var result = new List<SqlSnippetPlaceholder>(names.Count);

        foreach (var name in names)
        {
            result.Add(byId.TryGetValue(name, out var kept)
                ? new SqlSnippetPlaceholder(name, kept.DefaultValue, kept.ToolTip)
                : new SqlSnippetPlaceholder(name));
        }

        return result;
    }

    internal static bool IsNameStart(char value) => char.IsLetter(value) || value == '_';

    internal static bool IsNamePart(char value) => char.IsLetterOrDigit(value) || value == '_';
}
