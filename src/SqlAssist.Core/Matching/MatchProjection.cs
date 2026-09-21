using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary><see cref="MatchProjection"/> 找片段時的比對方式。</summary>
[Flags]
public enum MatchProjectionMode
{
    None = 0,

    /// <summary>取最後一次出現，而不是第一次。</summary>
    FromEnd = 1,

    /// <summary>前後都必須是詞界；字母、數字與底線算字的一部分。</summary>
    WholeWord = 2,

    IgnoreCase = 4
}

/// <summary>
/// 把落在片段上的命中區段平移到整份文字裡。
/// </summary>
/// <remarks>
/// 命中是在一小段文字上算出來的（名稱本體、或從定義本文裁出來的那一行），而要畫高亮的表面
/// 手上是整份文字。索引不換算的症狀是每一段高亮都畫在整份文字最前面那幾個字上，
/// 而那看起來像是比對錯了——比不畫更難解釋，所以<b>對不上就整組放棄</b>，不猜位置。
///
/// 與領域無關：只認得「整份文字」「片段」與區段，不知道那是 SQL 還是別的東西，
/// <c>Core/Matching</c> 的既有規則如此。
/// </remarks>
public static class MatchProjection
{
    /// <summary>片段在整份文字裡的位置；找不到回傳 -1。</summary>
    /// <param name="start">從哪裡開始找；之前的部分連找都不找。</param>
    public static int Find(string text, string fragment, int start, MatchProjectionMode mode)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (fragment is null) throw new ArgumentNullException(nameof(fragment));
        if (fragment.Length == 0 || start < 0 || start > text.Length - fragment.Length) return -1;

        var comparison = (mode & MatchProjectionMode.IgnoreCase) != 0
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var wholeWord = (mode & MatchProjectionMode.WholeWord) != 0;
        var fromEnd = (mode & MatchProjectionMode.FromEnd) != 0;
        var at = start;
        var last = -1;

        while (at <= text.Length - fragment.Length)
        {
            var found = text.IndexOf(fragment, at, comparison);
            if (found < 0) break;

            if (!wholeWord || IsWholeWord(text, found, fragment.Length))
            {
                if (!fromEnd) return found;
                last = found;
            }

            // 前進一個字元而不是整段：重疊的出現也是出現，而且詞界不合的那一次之後
            // 可能緊接著一次合格的。
            at = found + 1;
        }

        return last;
    }

    /// <summary>片段在整份文字裡的<b>每一次</b>出現，由前到後；找不到時是空的。</summary>
    /// <remarks>
    /// 與 <see cref="Find"/> 同一套詞界與大小寫規則，差別只在它不在第一次就停。
    /// <see cref="MatchProjectionMode.FromEnd"/> 在這裡沒有意義——全部都回，取最後一次
    /// 只是取清單的最後一個，所以那個位元被忽略。
    ///
    /// 重疊的出現照收：前進一個字元而不是整段，理由與 <see cref="Find"/> 相同，
    /// 而且要不要去掉重疊是呼叫端的事——它才知道兩段重疊的高亮要併成一段還是各算一個。
    /// </remarks>
    /// <param name="limit">
    /// 最多回幾個，湊滿就<b>不再往下掃</b>。上限落在這裡而不是呼叫端收到之後才截斷：
    /// 一份幾千行的定義本文裡同一個字出現幾百次是常態，而掃完再丟等於把那幾百個位置
    /// 先配置出來。預設沒有上限——名稱那一種候選本來就只有幾十個字元。
    /// </param>
    public static IReadOnlyList<int> FindAll(
        string text, string fragment, int start, MatchProjectionMode mode, int limit = int.MaxValue)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (fragment is null) throw new ArgumentNullException(nameof(fragment));
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (limit == 0 || fragment.Length == 0 || start < 0 || start > text.Length - fragment.Length)
        {
            return Array.Empty<int>();
        }

        var comparison = (mode & MatchProjectionMode.IgnoreCase) != 0
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var wholeWord = (mode & MatchProjectionMode.WholeWord) != 0;
        var found = new List<int>();
        var at = start;

        while (at <= text.Length - fragment.Length)
        {
            var index = text.IndexOf(fragment, at, comparison);
            if (index < 0) break;

            if (!wholeWord || IsWholeWord(text, index, fragment.Length))
            {
                found.Add(index);
                if (found.Count == limit) break;
            }

            at = index + 1;
        }

        return found;
    }

    /// <summary>
    /// 把區段整組平移 <paramref name="offset"/>；任何一段落在範圍外就整組放棄。
    /// </summary>
    /// <param name="fragmentLength">區段原本落在哪一段文字上；超出它的區段代表兩邊對不起來。</param>
    /// <param name="textLength">平移之後要落在哪一份文字裡。</param>
    public static IReadOnlyList<MatchSpan> Shift(
        IReadOnlyList<MatchSpan> spans, int offset, int fragmentLength, int textLength)
    {
        if (spans is null) throw new ArgumentNullException(nameof(spans));
        if (spans.Count == 0 || offset < 0) return Array.Empty<MatchSpan>();

        var shifted = new MatchSpan[spans.Count];

        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];

            if (span.End > fragmentLength || offset + span.End > textLength)
            {
                return Array.Empty<MatchSpan>();
            }

            shifted[index] = new MatchSpan(span.Start + offset, span.Length);
        }

        return shifted;
    }

    /// <remarks>
    /// 詞界照識別字的形狀認：字母、數字與底線是字的一部分，其餘都是邊界。方括號因此算邊界，
    /// 同一個名稱不論指令碼寫成 <c>[CopyNo]</c> 還是 <c>CopyNo</c> 都對得上——風格選項會換掉
    /// 方括號，各記一種寫法的話，換過風格就不再高亮。
    /// </remarks>
    private static bool IsWholeWord(string text, int start, int length)
    {
        if (start > 0 && IsWordCharacter(text[start - 1])) return false;

        var after = start + length;
        return after >= text.Length || !IsWordCharacter(text[after]);
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
