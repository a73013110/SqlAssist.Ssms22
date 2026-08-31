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
    /// <summary>游標落點標記的名稱；完整標記見 <see cref="SqlSnippet.CaretMarker"/>。</summary>
    internal const string EndId = "end";

    /// <summary>原生 Expansion Engine 的選取文字標記名稱。</summary>
    internal const string SelectedId = "selected";

    /// <summary>系統識別字，不能當佔位符 ID。</summary>
    internal static bool IsReserved(string id) =>
        IsNamed(id, EndId) || IsNamed(id, SelectedId);

    internal static bool IsNamed(string id, string reserved) =>
        string.Equals(id, reserved, StringComparison.OrdinalIgnoreCase);

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

        List<string>? names = null;
        HashSet<string>? seen = null;
        var index = 0;

        while (index < code!.Length)
        {
            if (code[index] != '$' || !TryReadMarker(code, index, out var name, out var end))
            {
                index++;
                continue;
            }

            // 從結尾的 $ 之後繼續：$a$$b$ 的第二個佔位符要認得出來。
            index = end;

            if (IsReserved(name))
            {
                continue;
            }

            // 大多數片段只有幾個欄位，而沒有欄位的片段完全不必配置。
            names ??= new List<string>(4);
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return (IReadOnlyList<string>?)names ?? Array.Empty<string>();
    }

    /// <summary>
    /// 讀出 <paramref name="open"/> 起的一個 <c>$名稱$</c> 標記。
    /// </summary>
    /// <param name="code">整段程式碼。</param>
    /// <param name="open">要檢查的位置，必須是一個錢字號。</param>
    /// <param name="id">標記名稱，讀不成標記時為空字串。</param>
    /// <param name="end">標記結束的下一個位置；讀不成時無意義。</param>
    /// <remarks>
    /// 標記語法只有這一份。<see cref="Extract"/> 與 <see cref="SqlSnippetExpansion"/>
    /// 一定要走同一個掃描器：兩份各自實作時，症狀是「管理介面列得出這個欄位，
    /// 展開時卻沒有被取代」，而兩邊看起來都對。
    ///
    /// <c>$$</c> 不是標記（名稱不能是空的），因此原樣留給呼叫端當字面錢字號處理。
    /// </remarks>
    internal static bool TryReadMarker(string code, int open, out string id, out int end)
    {
        id = string.Empty;
        end = open + 1;

        if (end >= code.Length || !IsNameStart(code[end]))
        {
            return false;
        }

        end++;

        while (end < code.Length && IsNamePart(code[end]))
        {
            end++;
        }

        if (end >= code.Length || code[end] != '$')
        {
            return false;
        }

        id = code.Substring(open + 1, end - open - 1);
        end++;
        return true;
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

    private static bool IsNameStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsNamePart(char value) => char.IsLetterOrDigit(value) || value == '_';
}
