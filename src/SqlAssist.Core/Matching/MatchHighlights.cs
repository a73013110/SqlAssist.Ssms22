using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 一份文字上要標出來的命中區段，以及每一個表面共用的標記上限。
/// </summary>
/// <remarks>
/// 位置一律由呼叫端交來的 <see cref="TextMatcher"/> 找：清單用哪一個比對器決定「這一列符不符合」，
/// 預覽就用同一個標出「符合在哪裡」。另寫一份的症狀是清單上有這一列，預覽卻一處都標不出來。
///
/// 上限落在<b>標記</b>上不落在比對上：重疊或相鄰的出現併成一段，算一處（在 <c>aaa</c> 裡找
/// <c>aa</c>），所以不交給 <see cref="TextMatcher.FindAll"/> 的 <c>limit</c>——那一個數的是出現次數，
/// 併完之後可能還不到上限卻已經停了。少標了幾處要由呼叫端說出來，理由見 docs/search-highlight.md。
/// </remarks>
public static class MatchHighlights
{
    /// <summary>
    /// 一份文字上最多標幾處。
    /// </summary>
    /// <remarks>
    /// 沒有上限的症狀不是慢，是整份文件被切成上萬段：一張寬表上有五十個資料行都叫得出使用者打的
    /// 那幾個字，而每一個又在擴充屬性裡再出現一次。所有顯示命中的表面共用這一個數。
    /// </remarks>
    public const int Maximum = 500;

    /// <summary>超過 <see cref="Maximum"/> 而少標了幾處時，狀態列上的那一句；每一個顯示命中的表面共用。</summary>
    /// <remarks>少標了卻不說的症狀是使用者按到最後一處就以為看完了。</remarks>
    public static string TruncatedNotice { get; } = "命中太多，只標出前 " + Maximum + " 處。";

    /// <summary><paramref name="matcher"/> 在 <paramref name="text"/> 上的區段，由前到後、不重疊。</summary>
    /// <param name="truncated">超過 <see cref="Maximum"/> 而少標了幾處；呼叫端要說出來。</param>
    public static IReadOnlyList<MatchSpan> Locate(TextMatcher matcher, string text, out bool truncated)
    {
        if (matcher is null) throw new ArgumentNullException(nameof(matcher));
        if (text is null) throw new ArgumentNullException(nameof(text));

        truncated = false;
        var length = matcher.Pattern.Length;
        List<MatchSpan>? spans = null;

        for (var at = matcher.IndexOf(text); at >= 0; at = matcher.IndexOf(text, at + 1))
        {
            // 與上一段重疊或緊貼就併進去：文件那一層要不重疊的區段，而兩段緊貼的底色看起來本來就是一段。
            if (spans is { Count: > 0 } && at <= spans[spans.Count - 1].End)
            {
                var last = spans[spans.Count - 1];
                spans[spans.Count - 1] = new MatchSpan(last.Start, Math.Max(last.End, at + length) - last.Start);
                continue;
            }

            if (spans?.Count == Maximum)
            {
                truncated = true;
                break;
            }

            (spans ??= new List<MatchSpan>()).Add(new MatchSpan(at, length));
        }

        return spans ?? (IReadOnlyList<MatchSpan>)Array.Empty<MatchSpan>();
    }
}
