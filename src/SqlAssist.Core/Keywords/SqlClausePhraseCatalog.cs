using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 產生出來的子句片語，以及「游標前面是哪一個片語」的比對。
/// </summary>
/// <remarks>
/// 比對是資料驅動的：新增一個片語只要在產生器 <c>Data</c> 資料夾的宣告加一行再重跑，
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

    /// <summary>
    /// 標頭以名稱結尾的清單片語（<c>BACKUP DATABASE {name} ,*</c>：檔案清單緊接資料庫名稱，前面沒有開清單的字）；項數多的排前面。
    /// </summary>
    private static readonly SqlClausePhrase[] ListsAfterName = FindListsAfterName();

    /// <summary>沒有尾巴、只認游標處位置的片語；附加片語另列。</summary>
    private static readonly SqlClausePhrase[] AtPosition =
        Phrases.Where(phrase => phrase.Length == 0 && !phrase.IsAdditive).ToArray();

    /// <summary>只認位置的附加片語，依產生器的順序；帶尾巴的附加片語與一般片語一樣以尾巴比對。</summary>
    private static readonly SqlClausePhrase[] Additive = Phrases.Where(phrase => phrase.IsAdditive && phrase.Length == 0).ToArray();

    /// <summary>同時對上的附加片語合成的片語，鍵是它們在 <see cref="Additive"/> 裡的位元；建議項的 Tag 要一直是同一個物件。</summary>
    private static readonly ConcurrentDictionary<long, SqlClausePhrase> AdditiveUnions = new();

    /// <summary>帶尾巴的附加片語與游標處只認位置的附加片語合成的片語；理由同 <see cref="AdditiveUnions"/>。</summary>
    private static readonly ConcurrentDictionary<(SqlClausePhrase Tail, SqlClausePhrase Position), SqlClausePhrase> TailAdditiveUnions = new();

    /// <summary>只認位置的片語與游標處疊著的其他位置的附加片語合成的片語；理由同 <see cref="AdditiveUnions"/>。</summary>
    private static readonly ConcurrentDictionary<(SqlClausePhrase Position, SqlClausePhrase Stacked), SqlClausePhrase> StackedUnions = new();

    /// <summary>同一條尾巴在幾個位置同時確定成立時合成的片語，鍵是尾巴與那幾個位置；理由同 <see cref="AdditiveUnions"/>。</summary>
    private static readonly ConcurrentDictionary<(string Pattern, SqlKeywordPosition After), SqlClausePhrase> PositionUnions = new();

    /// <summary>寫完一句的片語是 IF 單句主體時併上 <see cref="IfBodyEndWords"/> 的片語，鍵是原本的片語；理由同 <see cref="AdditiveUnions"/>。</summary>
    private static readonly ConcurrentDictionary<SqlClausePhrase, SqlClausePhrase> IfBodyUnions = new();

    /// <summary>IF 單句主體寫完比語句開頭多接的字（ELSE），取自關鍵字目錄。</summary>
    private static readonly string[] IfBodyEndWords = SqlKeywordCatalog.All
        .Where(keyword => (SqlKeywordCatalog.GetPositions(keyword) &
                           (SqlKeywordPosition.IfBodyEnd | SqlKeywordPosition.StatementStart)) == SqlKeywordPosition.IfBodyEnd)
        .ToArray();

    /// <summary>任一個片語接得上的字；語句開頭的判準先問它，絕大多數的字不必比對。</summary>
    private static readonly HashSet<string> AllWords =
        new(Phrases.SelectMany(phrase => phrase.Words).Select(SqlClausePhrase.FirstWord), StringComparer.OrdinalIgnoreCase);

    /// <summary>全部片語。</summary>
    public static IReadOnlyList<SqlClausePhrase> All => Phrases;

    /// <summary>
    /// 游標前面是哪一個片語；比對不到時回傳 null。
    /// </summary>
    /// <param name="analyzer">同一段詞元的位置分析：片語前一格的位置與 <c>...</c> 的動詞都問它。</param>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="textBeforeToken">同一段原文；前一格的位置要看換行。</param>
    /// <param name="caret">位置分析對游標處的回報；沒有尾巴的片語拿它當前一格。</param>
    /// <param name="listAnchor">
    /// 游標在清單片語開的選項清單裡時，位置分析走訪到的錨點（標頭最後一個詞元）；不在清單裡是 -1。
    /// </param>
    /// <remarks>
    /// 語句到片語為止已經完整、游標又換了行時不算，見 <see cref="SqlClausePhrase.EndsStatement"/>；
    /// 游標處判不出位置時只算可能。
    /// 片語前一格的位置過不了 <see cref="SqlClausePhrase.After"/> 時也不算。
    ///
    /// 同時比對得上時取項數多的：<c>OFFSET 0 ROWS </c> 是 <c>OFFSET {value} ROWS</c>
    /// 而不是視窗框架的 <c>ROWS</c>；<c>SELECT TOP 10 WITH </c> 的 WITH 前一格是 TOP 子句，
    /// 不是觸發程序標頭。項數一樣多的只有同一條尾巴在不同位置上的片語：前一格判不出位置時取字多的
    /// ——多列幾個字，不少列；前一格同時是幾個位置時（<c>ORDER BY a⏎FETCH</c> 是查詢的尾端，換了行也是下一句的開頭）
    /// 每個位置接的字都算，見 <see cref="Union"/>。
    ///
    /// 沒有尾巴的片語項數是零，只在尾巴都比對不到、或只比對到<b>可能</b>時才輪到：它的前一格
    /// 就是游標處，判得出來就是確定的。游標處判不出位置時它什麼也沒認到，不算。
    /// 清單片語排在它們之前：位置分析交出錨點時，游標在哪一句的清單裡是確定的。
    /// </remarks>
    internal static SqlClausePhraseMatch? Match(
        SqlKeywordPositionAnalyzer analyzer,
        IReadOnlyList<SqlToken> tokens,
        string textBeforeToken,
        SqlKeywordPosition caret,
        int listAnchor = -1)
    {
        var onNewLine = tokens.Count > 0 &&
            SqlKeywordPositionAnalyzer.StartsOnNewLine(tokens[tokens.Count - 1].End, textBeforeToken.Length, textBeforeToken);

        return Match(tokens, tokens.Count, onNewLine, caret, listAnchor, analyzer);
    }

    /// <summary>
    /// <paramref name="anchor"/> 是某個清單片語標頭的最後一個詞元，而且標頭前一格確定對得上。
    /// </summary>
    /// <param name="analyzer">呼叫端的位置分析，回答標頭前一格的位置與 <c>...</c> 的動詞。</param>
    /// <remarks>
    /// 位置分析拿它認清單的錨點：哪些敘述有這種清單只由片語說一次，分析器不另列一份。
    /// 前一格判不出位置時不算：那一句可能根本不是這個意思，當成清單會封閉掉別的字。
    /// </remarks>
    internal static bool OpensList(IReadOnlyList<SqlToken> tokens, int anchor, SqlKeywordPositionAnalyzer analyzer)
    {
        return MatchList(tokens, anchor, analyzer) is { IsCertain: true };
    }

    /// <summary>
    /// 前 <paramref name="count"/> 個詞元寫到那裡，比對到的片語確定接得上 <paramref name="keyword"/>。
    /// </summary>
    /// <param name="before">位置分析對那一格的回報，不含換行補上的語句開頭。</param>
    /// <param name="analyzer">呼叫端的位置分析，回答片語前一格的位置與 <c>...</c> 的動詞。</param>
    /// <remarks>
    /// 語句開頭的判準拿它分辨隱含的界線：剖析器不看換行，接得上的字就屬於前一句，
    /// 所以這裡不像 <see cref="Match(SqlKeywordPositionAnalyzer, IReadOnlyList{SqlToken}, string, SqlKeywordPosition, int)"/> 那樣
    /// 在換行後略過已經完整的片語。只算確定的比對：前一格判不出來時，片語那個意思可能根本不成立。
    /// </remarks>
    internal static bool Continues(
        IReadOnlyList<SqlToken> tokens,
        int count,
        SqlKeywordPosition before,
        SqlKeywordPositionAnalyzer analyzer,
        string keyword)
    {
        return AllWords.Contains(keyword) &&
            Match(tokens, count, onNewLine: false, before, listAnchor: -1, analyzer) is { IsCertain: true } match &&
            match.Phrase.Offers(keyword);
    }

    private static SqlClausePhraseMatch? Match(
        IReadOnlyList<SqlToken> tokens,
        int count,
        bool onNewLine,
        SqlKeywordPosition caret,
        int listAnchor,
        SqlKeywordPositionAnalyzer analyzer)
    {
        SqlClausePhraseMatch? best = null;

        if (count > 0)
        {
            var last = tokens[count - 1];
            var lastWord = last.Kind == SqlTokenKind.Identifier && !last.IsQuoted ? last.Value
                : last.Kind == SqlTokenKind.Operator && last.Value == "=" ? last.Value
                : last.IsPunctuation(",") || last.IsPunctuation("(") ? last.Value
                : null;

            if (lastWord is not null && ByLastWord.TryGetValue(lastWord, out var candidates))
            {
                best = FirstMatch(candidates, tokens, count, onNewLine, caret, analyzer, minimumLength: 0);
            }

            best = FirstMatch(EndingWithPlaceholder, tokens, count, onNewLine, caret, analyzer, best?.Phrase.Length + 1 ?? 0) ?? best;
        }

        if (best is { IsCertain: true })
        {
            return best;
        }

        // 判不出位置時整份目錄進場，附加片語的字也一樣：None 的附加片語只在這裡出現。
        // 寫完一句的尾巴只比對到可能（換了行）時，游標處可能是下一句的開頭，附加片語的字一併加：
        // END CONVERSATION 10⏎ 之後照樣寫得出 ENABLE。
        if (caret == SqlKeywordPosition.Any)
        {
            return best is { Phrase.EndsStatement: true } ? UnionAdditive(best, MatchAdditive(caret)) : best ?? MatchAdditive(caret);
        }

        // 錨點之後緊接的第一格由標頭本身那個片語說（CREATE LOGIN 的第一項只能是 PASSWORD），上面已比對過。
        if (listAnchor >= 0 && listAnchor < count - 1 && MatchList(tokens, listAnchor, analyzer) is { } listed)
        {
            return listed;
        }

        // 附加片語排在最後，只在什麼都沒比對到時才輪到：它只加字，不該擠掉一個可能的尾巴。
        // 帶尾巴的附加片語也只加字，兩種同時對上時取聯集（開模組本體的 AS 之後，EXTERNAL 與 ENABLE、COPY 都在）。
        return FirstMatch(AtPosition, tokens, count, onNewLine, caret, analyzer, minimumLength: 0) is { } position
            ? WithStackedAdditive(position, caret)
            : WithAdditive(best, caret);
    }

    /// <summary>
    /// 只認位置的片語對上了，游標處還疊著它不管的位置時，那些位置的附加片語照樣加字；確定與封閉照位置片語。
    /// </summary>
    /// <remarks>
    /// 位置片語只說它自己那一格：資料行的預設值寫完（<c>a int DEFAULT @d |</c>）是資料行定義的尾端，也是運算元之後。
    /// 確定的位置片語讓整份目錄退場，只取它的話運算元之後的 AT 就不見了。
    /// </remarks>
    private static SqlClausePhraseMatch WithStackedAdditive(SqlClausePhraseMatch match, SqlKeywordPosition caret)
    {
        if (caret == SqlKeywordPosition.Any || MatchAdditive(caret & ~match.Phrase.After) is not { } stacked)
        {
            return match;
        }

        var phrase = StackedUnions.GetOrAdd((match.Phrase, stacked.Phrase), pair => new SqlClausePhrase(
            pair.Position.Pattern,
            pair.Position.After,
            pair.Position.Probe,
            pair.Position.IsClosed,
            pair.Position.EndsStatement,
            pair.Position.Words.Concat(pair.Stacked.Words).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            takesVariable: pair.Position.TakesVariable,
            takesName: pair.Position.TakesName));

        return match.IsCertain ? phrase.Certain : phrase.Tentative;
    }

    private static SqlClausePhraseMatch? WithAdditive(SqlClausePhraseMatch? best, SqlKeywordPosition caret)
    {
        if (best is not null && !best.Phrase.IsAdditive)
        {
            return best;
        }

        return UnionAdditive(best, MatchAdditive(caret));
    }

    /// <summary>尾巴與游標處的附加片語都對上時合成一個：字取聯集，只加字、比對只算可能。</summary>
    private static SqlClausePhraseMatch? UnionAdditive(SqlClausePhraseMatch? best, SqlClausePhraseMatch? position)
    {
        if (best is null || position is null)
        {
            return best ?? position;
        }

        return TailAdditiveUnions.GetOrAdd((best.Phrase, position.Phrase), pair => new SqlClausePhrase(
            pair.Tail.Pattern,
            pair.Tail.After,
            pair.Tail.Probe,
            isClosed: false,
            endsStatement: false,
            pair.Tail.Words.Concat(pair.Position.Words).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            isAdditive: true)).Tentative;
    }

    /// <summary>游標處的位置接得上的附加片語；對上幾個就取它們的字的聯集。</summary>
    /// <remarks>
    /// 位置的比對與關鍵字同一條規則（<see cref="SqlKeywordPositionExtensions.Allows"/>）：<c>None</c> 的附加片語
    /// 只在判不出位置時出現，判不出位置時每一個附加片語都算。
    ///
    /// 位置是旗標，一格可以同時是幾個位置：<c>SELECT SUM(a) </c> 是選取清單尾端（<c>AT</c>），也是函式呼叫之後
    /// （<c>WITHIN</c>）。附加片語只加字，同時成立的全部都算；只取第一個的話另一個的字就不見了。
    /// </remarks>
    private static SqlClausePhraseMatch? MatchAdditive(SqlKeywordPosition caret)
    {
        var mask = 0L;

        for (var index = 0; index < Additive.Length; index++)
        {
            if (Additive[index].After.Allows(caret))
            {
                mask |= 1L << index;
            }
        }

        return mask == 0 ? null : AdditiveUnions.GetOrAdd(mask, CombineAdditive).Tentative;
    }

    private static SqlClausePhrase CombineAdditive(long mask)
    {
        var parts = Additive.Where((_, index) => (mask & (1L << index)) != 0).ToArray();

        if (parts.Length == 1)
        {
            return parts[0];
        }

        var after = parts.Aggregate(SqlKeywordPosition.None, (union, phrase) => union | phrase.After);
        var words = parts.SelectMany(phrase => phrase.Words).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new SqlClausePhrase(string.Empty, after, parts[0].Probe, isClosed: false, endsStatement: false, words, isAdditive: true);
    }

    private static SqlClausePhraseMatch? MatchList(
        IReadOnlyList<SqlToken> tokens,
        int anchor,
        SqlKeywordPositionAnalyzer analyzer)
    {
        var token = tokens[anchor];

        if (token.Kind == SqlTokenKind.Identifier && !token.IsQuoted && ListsByAnchor.TryGetValue(token.Value, out var candidates) &&
            MatchHead(candidates, tokens, anchor, analyzer) is { } listed)
        {
            return listed;
        }

        return token.Kind is SqlTokenKind.Identifier or SqlTokenKind.Variable
            ? MatchHead(ListsAfterName, tokens, anchor, analyzer)
            : null;
    }

    private static SqlClausePhraseMatch? MatchHead(
        SqlClausePhrase[] candidates,
        IReadOnlyList<SqlToken> tokens,
        int anchor,
        SqlKeywordPositionAnalyzer analyzer)
    {
        foreach (var phrase in candidates)
        {
            var start = phrase.MatchHead(tokens, anchor, analyzer);

            if (start >= 0 && Qualify(phrase, analyzer.PositionBefore(start)) is { } match)
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
        SqlKeywordPositionAnalyzer analyzer,
        int minimumLength)
    {
        for (var index = 0; index < candidates.Length; index++)
        {
            var phrase = candidates[index];

            if (phrase.Length < minimumLength)
            {
                break;
            }

            var start = phrase.MatchTail(tokens, count, analyzer);
            var before = start == count ? caret : analyzer.PositionBefore(start);

            // 語句到片語為止已經完整又換了行：可能是下一句，也可能還是這一句（CREATE SEQUENCE s⏎START WITH、
            // CREATE DATABASE d ON (…)⏎LOG ON），片語只算可能、字加進那一格。換行是不是界線由位置分析決定，這裡不另判：
            // 游標處的位置因換行補上了語句開頭（ALTER TABLE t ADD a int⏎），片語的探測文字寫不完一句也一樣。
            if (start >= 0 && Qualify(phrase, before) is { } match)
            {
                if (match.IsCertain)
                {
                    phrase = Union(phrase, candidates, index, before);
                    match = phrase.Certain;
                }

                if (phrase.EndsStatement && analyzer.EndsIfBodyAt(count - 1))
                {
                    phrase = IfBodyUnions.GetOrAdd(phrase, EndIfBody);
                    match = match.IsCertain ? phrase.Certain : phrase.Tentative;
                }

                return onNewLine && (phrase.EndsStatement || NewLineStartsStatement(caret, phrase)) ? phrase.Tentative : match;
            }
        }

        return null;
    }

    /// <summary>
    /// 寫完一句的片語是 IF 只有一句的主體：字併上 IF 主體寫完多接的字。
    /// </summary>
    /// <remarks>
    /// 片語說這一句寫完了，位置分析說不出來（<c>IF 1 = 1 COMMIT </c> 是 <c>Any</c>），游標處就沒有
    /// <see cref="SqlKeywordPosition.IfBodyEnd"/>；比對確定時這一格的字又只來自片語，ELSE 兩邊都落空。
    /// 只併 IF 主體比語句開頭多出來的字：同一行的下一句本來就不在寫完一句的片語裡，有沒有 IF 都一樣。
    /// </remarks>
    private static SqlClausePhrase EndIfBody(SqlClausePhrase phrase) => new(
        phrase.Pattern,
        phrase.After,
        phrase.Probe,
        phrase.IsClosed,
        endsStatement: true,
        phrase.Words.Concat(IfBodyEndWords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        takesVariable: phrase.TakesVariable,
        takesName: phrase.TakesName);

    /// <summary>游標處的位置含語句開頭，而片語不是語句開頭的片語：那是換行補上的界線。</summary>
    private static bool NewLineStartsStatement(SqlKeywordPosition caret, SqlClausePhrase phrase) =>
        caret != SqlKeywordPosition.Any &&
        (caret & SqlKeywordPosition.StatementStart) != SqlKeywordPosition.None &&
        (phrase.After & SqlKeywordPosition.StatementStart) == SqlKeywordPosition.None;

    /// <summary>
    /// 同一條尾巴在 <paramref name="before"/> 的其他位置也確定成立的片語，與 <paramref name="phrase"/> 合成一個：字取聯集。
    /// </summary>
    /// <remarks>
    /// 位置是旗標，前一格可以同時是幾個位置：<c>ORDER BY a⏎FETCH </c> 接得上 <c>APPROX</c>（查詢的尾端），
    /// 也接得上 <c>NEXT</c>（換了行的下一句）。只取第一個的話另一個位置的字就不見了，與附加片語取聯集同一個道理。
    /// 都封閉才封閉；收變數、收名稱、寫完一句的任一個成立就算。
    /// </remarks>
    private static SqlClausePhrase Union(SqlClausePhrase phrase, SqlClausePhrase[] candidates, int index, SqlKeywordPosition before)
    {
        List<SqlClausePhrase>? parts = null;

        for (var other = index + 1; other < candidates.Length && candidates[other].Length == phrase.Length; other++)
        {
            var candidate = candidates[other];

            if (string.Equals(candidate.Pattern, phrase.Pattern, StringComparison.OrdinalIgnoreCase) &&
                Qualify(candidate, before) is { IsCertain: true })
            {
                (parts ??= new List<SqlClausePhrase> { phrase }).Add(candidate);
            }
        }

        if (parts is null)
        {
            return phrase;
        }

        var after = parts.Aggregate(SqlKeywordPosition.None, (union, part) => union | part.After);

        return PositionUnions.GetOrAdd((phrase.Pattern, after), _ => new SqlClausePhrase(
            phrase.Pattern,
            after,
            phrase.Probe,
            parts.All(part => part.IsClosed),
            parts.Any(part => part.EndsStatement),
            parts.SelectMany(part => part.Words).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            takesVariable: parts.Any(part => part.TakesVariable),
            takesName: parts.Any(part => part.TakesName)));
    }

    /// <summary>
    /// 尾巴已經對上，再看片語第一個字前面那一格（<paramref name="before"/>）過不過得了 <see cref="SqlClausePhrase.After"/>。
    /// </summary>
    /// <remarks>
    /// 判不出位置時算數但不確定，理由見 <see cref="SqlClausePhraseMatch"/>。
    /// 區塊開頭接的是語句，所以語句開頭的片語在那裡一樣成立：<c>BEGIN SET NOCOUNT ON</c>。
    /// 沒有尾巴的片語從游標處開始，前一格就是游標處的位置，不必再分析一次。
    /// 附加片語只加字，不看前一格的（Lead）也一樣：確定的話整份目錄讓給它，
    /// <c>@p t READONLY AS </c> 之後只剩 EXTERNAL。
    /// </remarks>
    private static SqlClausePhraseMatch? Qualify(SqlClausePhrase phrase, SqlKeywordPosition before)
    {
        if (phrase.After == SqlKeywordPosition.Any)
        {
            return phrase.IsAdditive ? phrase.Tentative : phrase.Certain;
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

        return phrase.IsAdditive ? phrase.Tentative : phrase.Certain;
    }

    private static SqlClausePhrase[] Build()
    {
        var data = SqlKeywordCatalogData.ClausePhrases;
        var additive = SqlKeywordCatalogData.AdditivePhrases;
        var phrases = new SqlClausePhrase[data.Length + additive.Length];

        for (var index = 0; index < data.Length; index++)
        {
            var (pattern, after, probe, closed, takesVariable, takesName, endsStatement, words) = data[index];
            phrases[index] = new SqlClausePhrase(
                pattern, after, probe, closed, endsStatement, words, takesVariable: takesVariable, takesName: takesName);
        }

        for (var index = 0; index < additive.Length; index++)
        {
            var (pattern, after, probe, words) = additive[index];
            phrases[data.Length + index] = new SqlClausePhrase(
                pattern, after, probe, isClosed: false, endsStatement: false, words, isAdditive: true);
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

            // 錨點是字面字：執行期拿游標前那個詞元的文字找桶。名稱另列，見 ListsAfterName。
            var anchor = ListAnchor(phrase);

            if (anchor == "{name}")
            {
                continue;
            }

            if (!(char.IsLetter(anchor[0]) || anchor[0] == '_'))
            {
                throw new FormatException($"Phrase '{phrase.Pattern}': a list head must end with a word or a name.");
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

    private static SqlClausePhrase[] FindListsAfterName()
    {
        return LongestFirst(Phrases.Where(phrase => phrase.IsList && ListAnchor(phrase) == "{name}").ToList());
    }

    /// <summary>清單片語標頭的最後一項。</summary>
    private static string ListAnchor(SqlClausePhrase phrase)
    {
        var items = phrase.Pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return items[items.Length - 2];
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
