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
    /// <param name="textBeforeToken">同一段原文；判斷一句的開頭要看換行。</param>
    /// <remarks>
    /// 語句到片語為止已經完整、游標又換了行時不算，見 <see cref="SqlClausePhrase.EndsStatement"/>。
    ///
    /// 同時比對得上時取項數多的：<c>OFFSET 0 ROWS </c> 是 <c>OFFSET {value} ROWS</c>
    /// 而不是視窗框架的 <c>ROWS</c>。項數一樣多時兩個片語都在說同一條尾巴，那是
    /// 產生器的片語表寫重了，由測試擋下。
    /// </remarks>
    public static SqlClausePhrase? Match(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
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
        SqlClausePhrase? best = null;

        if (last.Kind == SqlTokenKind.Identifier &&
            !last.IsQuoted &&
            ByLastWord.TryGetValue(last.Value, out var candidates))
        {
            best = FirstMatch(candidates, tokens, textBeforeToken, onNewLine, minimumLength: 0);
        }

        return FirstMatch(EndingWithPlaceholder, tokens, textBeforeToken, onNewLine, best?.Length + 1 ?? 0) ?? best;
    }

    private static SqlClausePhrase? FirstMatch(
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

            if (start < 0)
            {
                continue;
            }

            if (phrase.StartsStatement &&
                !SqlKeywordPositionAnalyzer.StartsStatementAt(tokens, start, textBeforeToken))
            {
                continue;
            }

            return phrase;
        }

        return null;
    }

    private static SqlClausePhrase[] Build()
    {
        var data = SqlKeywordCatalogData.ClausePhrases;
        var phrases = new SqlClausePhrase[data.Length];

        for (var index = 0; index < data.Length; index++)
        {
            var (pattern, probe, closed, endsStatement, words) = data[index];
            phrases[index] = new SqlClausePhrase(pattern, probe, closed, endsStatement, words);
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

    /// <remarks>OrderBy 是穩定排序：項數相同時保留產生器的順序。</remarks>
    private static SqlClausePhrase[] LongestFirst(List<SqlClausePhrase> phrases)
    {
        return phrases.OrderByDescending(phrase => phrase.Length).ToArray();
    }
}
