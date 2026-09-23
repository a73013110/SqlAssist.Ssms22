using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

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
}
