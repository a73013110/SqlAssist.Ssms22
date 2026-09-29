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

    /// <summary>清單片語，依標頭最後一個字分桶；桶內項數多的排前面。</summary>
    private static readonly Dictionary<string, SqlClausePhrase[]> ListsByAnchor = IndexListsByAnchor();

    /// <summary>沒有尾巴、只認游標處位置的片語。</summary>
    private static readonly SqlClausePhrase[] AtPosition = Phrases.Where(phrase => phrase.Length == 0).ToArray();

    /// <summary>任一個片語接得上的字；語句開頭的判準先問它，絕大多數的字不必比對。</summary>
    private static readonly HashSet<string> AllWords =
        new(Phrases.SelectMany(phrase => phrase.Words), StringComparer.OrdinalIgnoreCase);

    /// <summary>全部片語。</summary>
    public static IReadOnlyList<SqlClausePhrase> All => Phrases;

    /// <summary>
    /// 游標前面是哪一個片語；比對不到時回傳 null。
    /// </summary>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="textBeforeToken">同一段原文；前一格的位置要看換行。</param>
    /// <param name="caret">位置分析對游標處的回報；沒有尾巴的片語拿它當前一格。</param>
    /// <param name="listAnchor">
    /// 游標在清單片語開的選項清單裡時，位置分析走訪到的錨點（標頭最後一個詞元）；不在清單裡是 -1。
    /// </param>
    /// <remarks>
    /// 語句到片語為止已經完整、游標又換了行時不算，見 <see cref="SqlClausePhrase.EndsStatement"/>。
    /// 片語前一格的位置過不了 <see cref="SqlClausePhrase.After"/> 時也不算。
    ///
    /// 同時比對得上時取項數多的：<c>OFFSET 0 ROWS </c> 是 <c>OFFSET {value} ROWS</c>
    /// 而不是視窗框架的 <c>ROWS</c>；<c>SELECT TOP 10 WITH </c> 的 WITH 前一格是 TOP 子句，
    /// 不是觸發程序標頭。項數一樣多的只有同一條尾巴在不同位置上的片語，它們的位置
    /// 互不重疊，前一格判不出位置時才同時成立，那時取字多的——多列幾個字，不少列。
    ///
    /// 沒有尾巴的片語項數是零，只在尾巴都比對不到、或只比對到<b>可能</b>時才輪到：它的前一格
    /// 就是游標處，判得出來就是確定的。游標處判不出位置時它什麼也沒認到，不算。
    /// 清單片語排在它們之前：位置分析交出錨點時，游標在哪一句的清單裡是確定的。
    /// </remarks>
    internal static SqlClausePhraseMatch? Match(
        IReadOnlyList<SqlToken> tokens,
        string textBeforeToken,
        SqlKeywordPosition caret,
        int listAnchor = -1)
    {
        var onNewLine = tokens.Count > 0 &&
            SqlKeywordPositionAnalyzer.StartsOnNewLine(tokens[tokens.Count - 1].End, textBeforeToken.Length, textBeforeToken);

        return Match(
            tokens,
            tokens.Count,
            onNewLine,
            caret,
            listAnchor,
            start => SqlKeywordPositionAnalyzer.PositionBefore(tokens, start, textBeforeToken));
    }

    /// <summary>
    /// <paramref name="anchor"/> 是某個清單片語標頭的最後一個詞元，而且標頭前一格確定對得上。
    /// </summary>
    /// <param name="positionBefore">標頭第一個詞元前面那一格的位置；由呼叫端的分析器回答。</param>
    /// <remarks>
    /// 位置分析拿它認清單的錨點：哪些敘述有這種清單只由片語說一次，分析器不另列一份。
    /// 前一格判不出位置時不算：那一句可能根本不是這個意思，當成清單會封閉掉別的字。
    /// </remarks>
    internal static bool OpensList(IReadOnlyList<SqlToken> tokens, int anchor, Func<int, SqlKeywordPosition> positionBefore)
    {
        return MatchList(tokens, anchor, positionBefore) is { IsCertain: true };
    }

    /// <summary>
    /// 前 <paramref name="count"/> 個詞元寫到那裡，比對到的片語確定接得上 <paramref name="keyword"/>。
    /// </summary>
    /// <param name="before">位置分析對那一格的回報，不含換行補上的語句開頭。</param>
    /// <param name="positionBefore">片語第一個詞元前面那一格的位置；由呼叫端的分析器回答。</param>
    /// <remarks>
    /// 語句開頭的判準拿它分辨隱含的界線：剖析器不看換行，接得上的字就屬於前一句，
    /// 所以這裡不像 <see cref="Match(IReadOnlyList{SqlToken}, string, SqlKeywordPosition)"/> 那樣
    /// 在換行後略過已經完整的片語。只算確定的比對：前一格判不出來時，片語那個意思可能根本不成立。
    /// </remarks>
    internal static bool Continues(
        IReadOnlyList<SqlToken> tokens,
        int count,
        SqlKeywordPosition before,
        Func<int, SqlKeywordPosition> positionBefore,
        string keyword)
    {
        return AllWords.Contains(keyword) &&
            Match(tokens, count, onNewLine: false, before, listAnchor: -1, positionBefore) is { IsCertain: true } match &&
            match.Phrase.Offers(keyword);
    }

    private static SqlClausePhraseMatch? Match(
        IReadOnlyList<SqlToken> tokens,
        int count,
        bool onNewLine,
        SqlKeywordPosition caret,
        int listAnchor,
        Func<int, SqlKeywordPosition> positionBefore)
    {
        SqlClausePhraseMatch? best = null;

        if (count > 0)
        {
            var last = tokens[count - 1];

            if (last.Kind == SqlTokenKind.Identifier &&
                !last.IsQuoted &&
                ByLastWord.TryGetValue(last.Value, out var candidates))
            {
                best = FirstMatch(candidates, tokens, count, onNewLine, caret, positionBefore, minimumLength: 0);
            }

            best = FirstMatch(EndingWithPlaceholder, tokens, count, onNewLine, caret, positionBefore, best?.Phrase.Length + 1 ?? 0) ?? best;
        }

        if (best is { IsCertain: true } || caret == SqlKeywordPosition.Any)
        {
            return best;
        }

        // 錨點之後緊接的第一格由標頭本身那個片語說（CREATE LOGIN 的第一項只能是 PASSWORD），上面已比對過。
        if (listAnchor >= 0 && listAnchor < count - 1 && MatchList(tokens, listAnchor, positionBefore) is { } listed)
        {
            return listed;
        }

        // 附加片語排在最後，只在什麼都沒比對到時才輪到：它只加字，不該擠掉一個可能的尾巴。
        var atPosition = FirstMatch(AtPosition, tokens, count, onNewLine, caret, positionBefore, minimumLength: 0);
        return atPosition is { Phrase.IsAdditive: true } && best is not null ? best : atPosition ?? best;
    }

    private static SqlClausePhraseMatch? MatchList(
        IReadOnlyList<SqlToken> tokens,
        int anchor,
        Func<int, SqlKeywordPosition> positionBefore)
    {
        var token = tokens[anchor];

        if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted || !ListsByAnchor.TryGetValue(token.Value, out var candidates))
        {
            return null;
        }

        foreach (var phrase in candidates)
        {
            var start = phrase.MatchHead(tokens, anchor);

            if (start >= 0 && Qualify(phrase, positionBefore(start)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static SqlClausePhraseMatch? FirstMatch(
        SqlClausePhrase[] candidates,
        IReadOnlyList<SqlToken> tokens,
        int count,
        bool onNewLine,
        SqlKeywordPosition caret,
        Func<int, SqlKeywordPosition> positionBefore,
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

            var start = phrase.MatchTail(tokens, count);

            if (start >= 0 && Qualify(phrase, start == count ? caret : positionBefore(start)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// 尾巴已經對上，再看片語第一個字前面那一格（<paramref name="before"/>）過不過得了 <see cref="SqlClausePhrase.After"/>。
    /// </summary>
    /// <remarks>
    /// 判不出位置時算數但不確定，理由見 <see cref="SqlClausePhraseMatch"/>。
    /// 區塊開頭接的是語句，所以語句開頭的片語在那裡一樣成立：<c>BEGIN SET NOCOUNT ON</c>。
    /// 沒有尾巴的片語從游標處開始，前一格就是游標處的位置，不必再分析一次。
    /// </remarks>
    private static SqlClausePhraseMatch? Qualify(SqlClausePhrase phrase, SqlKeywordPosition before)
    {
        if (phrase.After == SqlKeywordPosition.Any)
        {
            return phrase.Certain;
        }

        if (before == SqlKeywordPosition.Any)
        {
            return phrase.Tentative;
        }

        if ((before & SqlKeywordPosition.BlockStart) != SqlKeywordPosition.None)
        {
            before |= SqlKeywordPosition.StatementStart;
        }

        if ((phrase.After & before) == SqlKeywordPosition.None)
        {
            return null;
        }

        // 附加片語只加字：確定的話整份目錄讓給它。
        return phrase.IsAdditive ? phrase.Tentative : phrase.Certain;
    }

    private static SqlClausePhrase[] Build()
    {
        var data = SqlKeywordCatalogData.ClausePhrases;
        var additive = SqlKeywordCatalogData.AdditivePhrases;
        var phrases = new SqlClausePhrase[data.Length + additive.Length];

        for (var index = 0; index < data.Length; index++)
        {
            var (pattern, after, probe, closed, endsStatement, words) = data[index];
            phrases[index] = new SqlClausePhrase(pattern, after, probe, closed, endsStatement, words);
        }

        for (var index = 0; index < additive.Length; index++)
        {
            var (after, probe, words) = additive[index];
            phrases[data.Length + index] = new SqlClausePhrase(
                string.Empty, after, probe, isClosed: false, endsStatement: false, words, isAdditive: true);
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
            if (phrase.LastWord is null && phrase.Length > 0 && !phrase.IsList)
            {
                phrases.Add(phrase);
            }
        }

        return LongestFirst(phrases);
    }

    private static Dictionary<string, SqlClausePhrase[]> IndexListsByAnchor()
    {
        var buckets = new Dictionary<string, List<SqlClausePhrase>>(StringComparer.OrdinalIgnoreCase);

        foreach (var phrase in Phrases)
        {
            if (!phrase.IsList)
            {
                continue;
            }

            // 錨點要是字面字：執行期拿游標前那個詞元的文字找桶。
            var items = phrase.Pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var anchor = items[items.Length - 2];

            if (!(char.IsLetter(anchor[0]) || anchor[0] == '_'))
            {
                throw new FormatException($"Phrase '{phrase.Pattern}': a list head must end with a word.");
            }

            if (!buckets.TryGetValue(anchor, out var bucket))
            {
                bucket = new List<SqlClausePhrase>();
                buckets[anchor] = bucket;
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
