using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Search;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 在定義本文裡找字，並裁出一段看得懂的前後文。
/// </summary>
/// <remarks>
/// 本文命中<b>不走</b> <see cref="FuzzyMatcher"/>：模糊比對是為識別字設計的，
/// 它允許字母散在整個候選裡，而一份幾千行的定義本文對任何三個字母的樣式都會命中，
/// 分數還很高。使用者在本文上要的是「這幾個字連在一起出現在哪裡」，
/// 所以這裡是字面子字串——與 SQL Memory 搜尋的那一種語意相同，也與名稱在使用者打開
/// 大小寫或全字之後的比對方式相同（見 <see cref="SearchIdentifierMatch"/>）。
/// </remarks>
internal static class SqlCatalogBodySearch
{
    /// <summary>單一物件最多記幾個命中位置。</summary>
    /// <remarks>
    /// 一個 <c>Loan</c> 在自己的預存程序裡出現一百次是常態，而畫面上一列只放得下
    /// 一段前後文。記下全部只是讓分數與配置跟著本文長度走。
    /// </remarks>
    internal const int MaximumMatches = 32;

    /// <summary>片段最多這麼長。</summary>
    internal const int MaximumSnippetLength = 160;

    /// <summary>命中前面留幾個字元的前後文。</summary>
    /// <remarks>
    /// 留一點而不是從命中處開始切：命中處往左那幾個字往往正是它的意思
    /// （<c>JOIN Loan</c> 與 <c>DROP TABLE Loan</c> 差在前面那幾個字）。
    /// </remarks>
    internal const int LeadingContext = 32;

    /// <summary>
    /// 找出所有命中位置；一個都沒有時回傳空清單。
    /// </summary>
    /// <param name="options">
    /// <see cref="SearchOptions.MatchCasing"/> 決定區不區分大小寫，
    /// <see cref="SearchOptions.WholeWord"/> 決定要不要檢查詞界；換算與名稱那一邊共用
    /// <see cref="SearchOptionsExtensions.ToProjectionMode"/>，兩處不會對同一個旗標各讀一種意思。
    /// </param>
    /// <remarks>
    /// 掃描本身在 <see cref="MatchProjection.FindAll"/>：詞界、重疊命中與大小寫那三條規則
    /// 本文與名稱都要用，各寫一份的症狀是其中一份把 <c>#</c> 算成識別字的一部分，
    /// 而使用者搜 <c>Loan</c> 時 <c>#Loan</c> 在名稱上收得到、在本文上收不到。
    ///
    /// 重疊的出現照收（在 <c>aaa</c> 裡找 <c>aa</c>）：使用者算的是「提到幾次」。
    /// </remarks>
    internal static IReadOnlyList<int> FindAll(string body, string text, SearchOptions options) =>
        MatchProjection.FindAll(body, text, 0, options.ToProjectionMode(), MaximumMatches);

    /// <summary>
    /// 裁出第一個命中所在的那一行，並把落在裁切範圍內的命中換算成高亮區段。
    /// </summary>
    /// <remarks>
    /// 位移一定要對得上裁切<b>之後</b>的字串：高亮是照 <see cref="MatchSpan.Start"/> 畫的，
    /// 交出本文裡的絕對位置會讓每一段高亮都畫在別的字上，而那看起來像是比對錯了。
    ///
    /// 也刻意<b>不</b>在前面補「…」之類的省略記號：那會讓每一個區段再加一次位移，
    /// 而漏掉一處的症狀與上一段一樣。要不要畫省略記號是呈現那一層的事。
    ///
    /// 只裁第一個命中那一行，一個物件回報一列。每一個命中各回一列的話，
    /// 一個提到二十次的預存程序會把整份清單佔滿，而那二十列指的是同一個物件。
    /// </remarks>
    internal static string BuildSnippet(
        string body, IReadOnlyList<int> matches, int length, out IReadOnlyList<MatchSpan> spans)
    {
        if (matches.Count == 0)
        {
            spans = Array.Empty<MatchSpan>();
            return string.Empty;
        }

        var first = matches[0];
        var lineStart = first == 0 ? 0 : body.LastIndexOf('\n', first - 1) + 1;
        var lineEnd = body.IndexOf('\n', first);

        if (lineEnd < 0)
        {
            lineEnd = body.Length;
        }

        // CRLF 的 '\r' 留著會在片段尾巴畫出一個看不見的字元，而它會被算進區段範圍。
        if (lineEnd > lineStart && body[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        var start = lineStart;
        var end = lineEnd;

        if (lineEnd - lineStart > MaximumSnippetLength)
        {
            start = Math.Max(lineStart, first - LeadingContext);
            end = Math.Min(lineEnd, start + MaximumSnippetLength);

            // 命中本身一定要整段留在片段裡，否則高亮會落在被切掉的地方。
            if (first + length > end)
            {
                end = Math.Min(lineEnd, first + length);
            }
        }

        var found = new List<MatchSpan>();

        foreach (var match in matches)
        {
            if (match >= start && match + length <= end)
            {
                found.Add(new MatchSpan(match - start, length));
            }
        }

        spans = found.ToArray();
        return body.Substring(start, end - start);
    }
}
