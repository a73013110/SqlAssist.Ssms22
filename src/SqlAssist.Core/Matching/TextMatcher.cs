using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 一個樣式照 <see cref="TextMatchOptions"/> 找字面出現；建立一次，拿去比很多份文字。
/// </summary>
/// <remarks>
/// 所有字面比對的唯一出處：SQL Search 的名稱、資料行、定義本文與高亮換算，
/// 以及 SQL Memory 的 SQL 全文、收藏名稱與說明。各寫一份的症狀是其中一份把 <c>#</c>
/// 算成識別字的一部分，或把「沒勾大小寫」讀成 ordinal，而同一個字在兩個工具窗、
/// 或同一筆的名稱與本文上命中的結果不一樣。
///
/// 兩條路、同一套規則：
/// <list type="bullet">
/// <item>字串走 <see cref="string.IndexOf(string, int, StringComparison)"/>，由執行階段做最佳化。</item>
/// <item>UTF-16LE 位元組（<see cref="IsMatchUtf16"/>）直接逐個 code unit 走 KMP，不先解碼：
/// 解碼會把未配對的 surrogate 換成 U+FFFD 而誤判命中，大段 SQL 也不必為了比一次多配置一份字串。</item>
/// </list>
/// 不分大小寫兩條路都等於「逐個 code unit 取 <see cref="char.ToUpperInvariant"/> 再 ordinal 比」，
/// 那正是 <see cref="StringComparison.OrdinalIgnoreCase"/> 的定義；兩條路一致由測試守住。
///
/// 重疊的出現照收（在 <c>aaa</c> 裡找 <c>aa</c>）：前進一個字元而不是整段，
/// 詞界不合的那一次之後可能緊接著一次合格的。空樣式什麼都不命中——「沒有輸入」要怎麼
/// 處理是呼叫端的事（列出全部，或整個停用搜尋），不是「每個位置都命中空字串」。
///
/// 不可變，可以同時在多個執行緒上用。
/// </remarks>
public sealed class TextMatcher
{
    private readonly StringComparison _comparison;
    private readonly string _folded;
    private readonly int[] _prefix;

    public TextMatcher(string pattern, TextMatchOptions options)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Options = TextMatchState.Require(options, nameof(options));
        IgnoreCase = (options & TextMatchOptions.MatchCasing) == 0;
        WholeWord = (options & TextMatchOptions.WholeWord) != 0;
        _comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _folded = IgnoreCase ? Fold(pattern) : pattern;
        _prefix = BuildPrefix(_folded);
    }

    public string Pattern { get; }

    public TextMatchOptions Options { get; }

    public bool IgnoreCase { get; }

    public bool WholeWord { get; }

    /// <summary>從 <paramref name="start"/> 起第一次合格的出現；找不到回傳 -1。</summary>
    public int IndexOf(string text, int start = 0)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (Pattern.Length == 0 || start < 0) return -1;

        for (var at = start; at <= text.Length - Pattern.Length; at++)
        {
            var found = text.IndexOf(Pattern, at, _comparison);
            if (found < 0) return -1;
            if (!WholeWord || IsWholeWord(text, found, Pattern.Length)) return found;
            at = found;
        }

        return -1;
    }

    /// <summary>從 <paramref name="start"/> 起最後一次合格的出現；找不到回傳 -1。</summary>
    /// <remarks>
    /// 由前往後掃而不是 <see cref="string.LastIndexOf(string, StringComparison)"/>：
    /// 重疊與詞界要照 <see cref="IndexOf"/> 同一條規則走，反著找的那一版在重疊時挑到的是另一處。
    /// </remarks>
    public int LastIndexOf(string text, int start = 0)
    {
        var last = -1;
        for (var found = IndexOf(text, start); found >= 0; found = IndexOf(text, found + 1)) last = found;
        return last;
    }

    /// <summary>每一次合格的出現，由前到後。</summary>
    /// <param name="limit">
    /// 最多回幾個，湊滿就<b>不再往下掃</b>：一份幾千行的定義本文裡同一個字出現幾百次是常態，
    /// 掃完再截等於先把那幾百個位置配置出來。
    /// </param>
    public IReadOnlyList<int> FindAll(string text, int start = 0, int limit = int.MaxValue)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));

        List<int>? found = null;
        for (var index = limit == 0 ? -1 : IndexOf(text, start); index >= 0; index = IndexOf(text, index + 1))
        {
            (found ??= new List<int>()).Add(index);
            if (found.Count == limit) break;
        }

        return found ?? (IReadOnlyList<int>)Array.Empty<int>();
    }

    /// <summary>有沒有任何一處合格的出現；null 視為沒有。</summary>
    public bool IsMatch(string? text) => text is not null && IndexOf(text) >= 0;

    /// <summary>
    /// 在 UTF-16LE 位元組裡找；與 <see cref="IsMatch"/> 同一套規則，不先解碼。
    /// </summary>
    /// <remarks>
    /// 奇數長度時最後那一個位元組湊不成 code unit，不參與比對。
    /// </remarks>
    public bool IsMatchUtf16(byte[] utf16)
    {
        if (utf16 is null) throw new ArgumentNullException(nameof(utf16));
        var length = utf16.Length / 2;
        var needle = _folded;
        if (needle.Length == 0 || length < needle.Length) return false;

        for (int at = 0, matched = 0; at < length; at++)
        {
            var value = CodeUnit(utf16, at);
            if (IgnoreCase) value = FoldChar(value);
            while (matched > 0 && value != needle[matched]) matched = _prefix[matched - 1];
            if (value == needle[matched]) matched++;
            if (matched < needle.Length) continue;

            var begin = at - needle.Length + 1;
            if (!WholeWord ||
                ((begin == 0 || !IsWordCharacter(CodeUnit(utf16, begin - 1))) &&
                 (at + 1 >= length || !IsWordCharacter(CodeUnit(utf16, at + 1)))))
            {
                return true;
            }

            // 詞界不合：退回最長的自我前綴繼續找，重疊的下一處仍可能合格。
            matched = _prefix[matched - 1];
        }

        return false;
    }

    /// <summary>字母、數字與底線算字的一部分，其餘都是詞界。</summary>
    /// <remarks>
    /// 照識別字的形狀認：方括號因此算邊界，同一個名稱不論寫成 <c>[CopyNo]</c> 還是
    /// <c>CopyNo</c> 都對得上——指令碼風格會換掉方括號，各記一種寫法的話換過風格就不再命中。
    /// </remarks>
    public static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsWholeWord(string text, int start, int length)
    {
        if (start > 0 && IsWordCharacter(text[start - 1])) return false;
        var after = start + length;
        return after >= text.Length || !IsWordCharacter(text[after]);
    }

    private static char CodeUnit(byte[] bytes, int index) =>
        (char)(bytes[index * 2] | (bytes[index * 2 + 1] << 8));

    /// <summary>與 <see cref="StringComparison.OrdinalIgnoreCase"/> 同一種折疊；ASCII 先走捷徑，熱迴圈裡最常見。</summary>
    private static char FoldChar(char value) =>
        value < 0x80
            ? value is >= 'a' and <= 'z' ? (char)(value - 0x20) : value
            : char.ToUpperInvariant(value);

    private static string Fold(string value)
    {
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++) chars[index] = FoldChar(chars[index]);
        return new string(chars);
    }

    private static int[] BuildPrefix(string needle)
    {
        var prefix = new int[needle.Length];
        for (int index = 1, matched = 0; index < needle.Length; index++)
        {
            while (matched > 0 && needle[index] != needle[matched]) matched = prefix[matched - 1];
            if (needle[index] == needle[matched]) matched++;
            prefix[index] = matched;
        }

        return prefix;
    }
}
