using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 產生出來的子句片語，以及「游標前面是哪一個片語」的比對。
/// </summary>
/// <remarks>
/// 比對是資料驅動的：新增一個片語只要在產生器的 <c>$ClausePhrases</c> 加一行再重跑，
/// 這裡不必改。不再各自為 <c>SET</c>、<c>ALTER INDEX</c>、<c>FOR XML</c> 寫一段位置判斷。
/// </remarks>
public static class SqlClausePhraseCatalog
{
    private static readonly SqlClausePhrase[] Phrases = Build();

    /// <summary>最後一項是字面值的片語，依那個字分桶；桶內項數多的排前面。</summary>
    private static readonly Dictionary<string, SqlClausePhrase[]> ByLastWord = IndexByLastWord();

    /// <summary>最後一項是名稱、值或括號的片語；項數多的排前面。</summary>
    private static readonly SqlClausePhrase[] EndingWithPlaceholder = FindEndingWithPlaceholder();

    /// <summary>全部片語。</summary>
    public static IReadOnlyList<SqlClausePhrase> All => Phrases;

    /// <summary>
    /// 游標前面是哪一個片語；比對不到時回傳 null。
    /// </summary>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="textBeforeToken">同一段原文；前一格的位置要看換行。</param>
    /// <remarks>
    /// 語句到片語為止已經完整、游標又換了行時不算，見 <see cref="SqlClausePhrase.EndsStatement"/>。
    /// 片語前一格的位置過不了 <see cref="SqlClausePhrase.After"/> 時也不算。
    ///
    /// 同時比對得上時取項數多的：<c>OFFSET 0 ROWS </c> 是 <c>OFFSET {value} ROWS</c>
    /// 而不是視窗框架的 <c>ROWS</c>；<c>CREATE TRIGGER tr ON t FOR </c> 是觸發程序的 FOR
    /// 而不是查詢之後的 FOR。項數一樣多的只有同一條尾巴在不同位置上的片語，它們的位置
    /// 互不重疊，前一格判不出位置時才同時成立，那時取字多的——多列幾個字，不少列。
    /// </remarks>
    public static SqlClausePhraseMatch? Match(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (textBeforeToken is null)
        {
            throw new ArgumentNullException(nameof(textBeforeToken));
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        var last = tokens[tokens.Count - 1];
        var onNewLine = SqlKeywordPositionAnalyzer.StartsOnNewLine(last.End, textBeforeToken.Length, textBeforeToken);
        SqlClausePhraseMatch? best = null;

        if (last.Kind == SqlTokenKind.Identifier &&
            !last.IsQuoted &&
            ByLastWord.TryGetValue(last.Value, out var candidates))
        {
            best = FirstMatch(candidates, tokens, textBeforeToken, onNewLine, minimumLength: 0);
        }

        return FirstMatch(EndingWithPlaceholder, tokens, textBeforeToken, onNewLine, best?.Phrase.Length + 1 ?? 0) ?? best;
    }

    private static SqlClausePhraseMatch? FirstMatch(
        SqlClausePhrase[] candidates,
        IReadOnlyList<SqlToken> tokens,
        string textBeforeToken,
        bool onNewLine,
        int minimumLength)
    {
        foreach (var phrase in candidates)
        {
            if (phrase.Length < minimumLength)
            {
                break;
            }

            if (phrase.EndsStatement && onNewLine)
            {
                continue;
            }

            var start = phrase.MatchTail(tokens);

            if (start >= 0 && Qualify(phrase, tokens, start, textBeforeToken) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// 尾巴已經對上，再看片語第一個字前面那一格過不過得了 <see cref="SqlClausePhrase.After"/>。
    /// </summary>
    /// <remarks>
    /// 判不出位置時算數但不確定，理由見 <see cref="SqlClausePhraseMatch"/>。
    /// 區塊開頭接的是語句，所以語句開頭的片語在那裡一樣成立：<c>BEGIN SET NOCOUNT ON</c>。
    /// </remarks>
    private static SqlClausePhraseMatch? Qualify(
        SqlClausePhrase phrase,
        IReadOnlyList<SqlToken> tokens,
        int start,
        string textBeforeToken)
    {
        if (phrase.After == SqlKeywordPosition.Any)
        {
            return phrase.Certain;
        }

        var before = SqlKeywordPositionAnalyzer.PositionBefore(tokens, start, textBeforeToken);

        if (before == SqlKeywordPosition.Any)
        {
            return phrase.Tentative;
        }

        if ((before & SqlKeywordPosition.BlockStart) != SqlKeywordPosition.None)
        {
            before |= SqlKeywordPosition.StatementStart;
        }

        return (phrase.After & before) != SqlKeywordPosition.None ? phrase.Certain : null;
    }

    private static SqlClausePhrase[] Build()
    {
        var data = SqlKeywordCatalogData.ClausePhrases;
        var phrases = new SqlClausePhrase[data.Length];

        for (var index = 0; index < data.Length; index++)
        {
            var (pattern, after, probe, closed, endsStatement, words) = data[index];
            phrases[index] = new SqlClausePhrase(pattern, after, probe, closed, endsStatement, words);
        }

        return phrases;
    }

    private static Dictionary<string, SqlClausePhrase[]> IndexByLastWord()
    {
        var buckets = new Dictionary<string, List<SqlClausePhrase>>(StringComparer.OrdinalIgnoreCase);

        foreach (var phrase in Phrases)
        {
            if (phrase.LastWord is not { } word)
            {
                continue;
            }

            if (!buckets.TryGetValue(word, out var bucket))
            {
                bucket = new List<SqlClausePhrase>();
                buckets[word] = bucket;
            }

            bucket.Add(phrase);
        }

        var index = new Dictionary<string, SqlClausePhrase[]>(buckets.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in buckets)
        {
            index[pair.Key] = LongestFirst(pair.Value);
        }

        return index;
    }

    private static SqlClausePhrase[] FindEndingWithPlaceholder()
    {
        var phrases = new List<SqlClausePhrase>();

        foreach (var phrase in Phrases)
        {
            if (phrase.LastWord is null)
            {
                phrases.Add(phrase);
            }
        }

        return LongestFirst(phrases);
    }

    /// <remarks>
    /// 項數相同時字多的在前，見 <see cref="Match"/>。OrderBy 是穩定排序，其餘保留產生器的順序。
    /// </remarks>
    private static SqlClausePhrase[] LongestFirst(List<SqlClausePhrase> phrases)
    {
        return phrases
            .OrderByDescending(phrase => phrase.Length)
            .ThenByDescending(phrase => phrase.Words.Count)
            .ToArray();
    }
}
