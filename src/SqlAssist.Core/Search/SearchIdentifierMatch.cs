using System;
using System.Collections.Generic;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.Search;

/// <summary>
/// 名稱與資料行那一種命中怎麼比：一份，所有 provider 共用。
/// </summary>
/// <remarks>
/// 兩種比對方式，由使用者開著哪幾個修飾決定，規則只有一條——<b>一個修飾都沒開才走模糊比對</b>：
/// <list type="bullet">
/// <item>都沒開：<see cref="FuzzyMatcher"/>，詞首加成、不分大小寫、字母可以散在候選各處。
/// 這是打字找東西時要的——<c>libr</c> 要找得到 <c>Lib_Reader</c>。</item>
/// <item>開了大小寫或全字：字面比對，樣式要整段連續出現在名稱裡，再照
/// <see cref="SearchOptionsExtensions.ToProjectionMode"/> 檢查大小寫與詞界。</item>
/// </list>
///
/// 兩顆修飾不能套在模糊比對上面：模糊命中的字母本來就是散開的，「前後是不是詞界」對一個
/// 散開的命中沒有答案，而使用者兩顆都開著、範圍也縮到只剩 Constraint，清單上仍然出現
/// <c>DF_Form_LeaveKind_isShow</c>（<c>f</c>…<c>i</c>…<c>n</c>…<c>i</c>…<c>s</c>…<c>h</c>
/// 剛好湊得出 <c>finish</c>）——那一筆說得出每一個字母在哪裡，卻不是他要找的東西。
/// 開關的意思是「我知道我要找的字長什麼樣」，而那正是字面比對。
///
/// 分數仍然向 <see cref="FuzzyMatcher"/> 要：字面命中也有好壞之分（落在詞首的比黏在字中間的
/// 該排前面），而自己另記一套分數的症狀是打開全字之後，剩下來那幾筆的<b>相對順序</b>也跟著變，
/// 使用者會以為清單換了一份答案。區段則一律用字面那幾段，不用模糊比對挑的那一組——
/// 高亮要標的是他打的那個字，不是湊得出那個字的幾個字母。
/// </remarks>
public static class SearchIdentifierMatch
{
    /// <summary>
    /// 比對一個識別字；沒命中時回 <see cref="FuzzyMatchResult.NoMatch"/>。
    /// </summary>
    /// <remarks>
    /// 字面那一條先問 <see cref="MatchProjection.FindAll"/> 再算分：<c>IndexOf</c> 比 DP 便宜得多，
    /// 而開著修飾的那一輪多數候選會在這一步就出局，整輪反而比不開修飾快。
    /// </remarks>
    public static FuzzyMatchResult Match(SearchQuery query, string candidate)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));

        // 沒有輸入的那一輪是「列出全部」，不是「每一個都字面命中空字串」。
        if (query.Options == SearchOptions.None || query.IsEmpty)
        {
            return FuzzyMatcher.MatchNormalized(query.NormalizedPattern, candidate);
        }

        var offsets = MatchProjection.FindAll(candidate, query.Text, 0, query.MatchMode);

        if (offsets.Count == 0) return FuzzyMatchResult.NoMatch;

        return FuzzyMatchResult.Matched(ScoreOf(query, candidate), SpansFor(offsets, query.Text.Length));
    }

    /// <remarks>
    /// 字面命中一定也是模糊命中（連續出現本來就是子序列），所以那一邊照理不會回
    /// <see cref="FuzzyMatchResult.NoMatch"/>；真的對不起來時（兩邊的大小寫折疊在某些字元上
    /// 不一致）用命中長度湊一個分數，而不是把這一筆丟掉——它確實字面命中了。
    /// </remarks>
    private static int ScoreOf(SearchQuery query, string candidate)
    {
        var fuzzy = FuzzyMatcher.MatchNormalized(query.NormalizedPattern, candidate);
        return fuzzy.IsMatch ? fuzzy.Score : query.Text.Length * FuzzyMatcher.ScoreMatch;
    }

    /// <summary>每一處出現各一段，重疊的併起來。</summary>
    /// <remarks>
    /// 重疊會發生在樣式自己疊得上自己的時候（在 <c>aaa</c> 裡找 <c>aa</c>）。
    /// <see cref="FuzzyMatchResult.Spans"/> 的合約是由小到大且互不重疊，交出兩段疊在一起的
    /// 區段會讓高亮那一層把同一塊文字切兩次，第二次的起點落在前一段裡面。
    /// </remarks>
    private static IReadOnlyList<MatchSpan> SpansFor(IReadOnlyList<int> offsets, int length)
    {
        var spans = new List<MatchSpan>(offsets.Count);

        foreach (var offset in offsets)
        {
            if (spans.Count != 0)
            {
                var last = spans[spans.Count - 1];

                if (offset <= last.End)
                {
                    spans[spans.Count - 1] = new MatchSpan(last.Start, offset + length - last.Start);
                    continue;
                }
            }

            spans.Add(new MatchSpan(offset, length));
        }

        return spans;
    }
}
