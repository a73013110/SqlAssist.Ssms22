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

    /// <summary>
    /// 包夾錨點的欄位名稱：有選取範圍時，選取的文字就填進這一格。
    /// </summary>
    /// <remarks>
    /// <b>刻意不是 <see cref="SelectedId"/>。</b><c>$selected$</c> 是原生 Expansion
    /// Engine 的保留字，宣告一個同名的 <c>Literal</c> 會與引擎自己的語意相撞，而
    /// 那條路要求在產生原生 XML 時多一層「欄位名稱換名」的映射——一份只為了沿用
    /// 別人的字而存在的映射，改錯了就是包夾安靜地填不進去。
    ///
    /// <c>surround</c> 對引擎來說只是一個普通欄位名稱，因此<b>沒有選取範圍時它就是
    /// 一格普通的 Tab Stop</b>，預設值與說明照舊——<c>be</c>、<c>wl</c> 這些片段
    /// 直接輸入捷徑展開的行為完全沒有改變。
    ///
    /// 「這一筆能不能包夾」因此不需要任何新欄位：樣板裡有沒有這一格就是答案，
    /// 使用者自訂片段把某一格命名成 <c>surround</c> 也就自動支援包夾。
    /// </remarks>
    public const string SurroundId = "surround";

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
    /// 檢查包夾錨點在這一份樣板裡最多只出現一次。
    /// </summary>
    /// <remarks>
    /// 出現兩次時，選取的內容會被<b>複製兩份</b>。同名欄位的同步是原生引擎用標記
    /// 做的，而包夾這一格填進去之後已經不是欄位，沒有人會替它同步——症狀是包完
    /// 之後多了一份一模一樣的程式碼，而樣板看起來完全合理。
    ///
    /// 內建片段由 <c>SqlSnippetDefaultsTests.包夾欄位在樣板裡只出現一次</c> 守著，
    /// 這一份則是同一條規則對<b>使用者自己寫的樣板</b>那一半：管理介面存檔前呼叫，
    /// 擋在寫進檔案之前，而不是等到某一次包夾才發作。
    /// </remarks>
    public static bool ValidateSurroundAnchor(string? code, out string error)
    {
        var count = 0;
        var index = 0;

        while (code is not null && index < code.Length)
        {
            if (code[index] != '$' || !TryReadMarker(code, index, out var name, out var end))
            {
                index++;
                continue;
            }

            index = end;

            if (IsNamed(name, SurroundId))
            {
                count++;
            }
        }

        if (count > 1)
        {
            error = $"包夾錨點 ${SurroundId}$ 只能出現一次，這一份出現了 {count} 次。";
            return false;
        }

        error = string.Empty;
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
