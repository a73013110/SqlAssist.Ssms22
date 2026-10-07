using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 一段「游標前面的尾巴」，以及文法在那之後接得上的字。
/// </summary>
/// <remarks>
/// <c>SET STATISTICS </c> 之後只接 <c>IO</c>、<c>TIME</c>、<c>XML</c>、<c>PROFILE</c>，
/// <c>ALTER INDEX i ON t </c> 之後只接 <c>REBUILD</c>、<c>REORGANIZE</c>…——這些字
/// ScriptDom 掃成識別字，關鍵字目錄收不到，位置旗標也切不到這麼細。片語與它的字由
/// <c>tools/Generate-Keywords.ps1</c> 以剖析器探測產生，這裡只負責比對與提供建議項。
///
/// 比對確定時，這一格的關鍵字<b>只</b>來自片語：<see cref="SqlKeywordPosition"/> 是給整個
/// 子句的粗分層，片語是比它更靠近游標的答案。<see cref="IsClosed"/> 再決定名稱與函式還
/// 能不能一起出現。確定與可能的分別見 <see cref="SqlClausePhraseMatch"/>。
/// </remarks>
public sealed class SqlClausePhrase
{
    private readonly Element[] _elements;
    private readonly HashSet<string> _wordSet;
    private readonly SqlLanguageCache<IReadOnlyList<SqlSuggestion>> _suggestions;

    internal SqlClausePhrase(
        string pattern,
        SqlKeywordPosition after,
        string probe,
        bool isClosed,
        bool endsStatement,
        string[] words,
        bool isAdditive = false,
        bool takesVariable = false,
        bool takesName = false)
    {
        Pattern = pattern;
        IsAdditive = isAdditive;
        After = after;
        Probe = probe;
        IsClosed = isClosed;
        TakesVariable = takesVariable;
        TakesName = takesName;
        EndsStatement = endsStatement;
        Words = words;
        _elements = Parse(pattern);
        Length = _elements.Count(element => element.Kind != ElementKind.Items);
        _wordSet = new HashSet<string>(Array.ConvertAll(words, FirstWord), StringComparer.OrdinalIgnoreCase);
        _suggestions = new SqlLanguageCache<IReadOnlyList<SqlSuggestion>>(_ => BuildSuggestions());
        Certain = new SqlClausePhraseMatch(this, isCertain: true);
        Tentative = new SqlClausePhraseMatch(this, isCertain: false);
    }

    /// <summary>片語的尾巴，寫法見 <c>tools/SqlAssist.KeywordGenerator/PhraseDeclaration.cs</c>。</summary>
    public string Pattern { get; }

    /// <summary>
    /// 那裡除了 <see cref="Words"/> 沒有別的東西是對的：剖析器在這一格不收任何名稱。
    /// </summary>
    /// <remarks>
    /// 字可以是零個：<c>SET ROWCOUNT </c> 之後要的是數字，清單一項都不該有。
    /// 不封閉的片語（<c>SET IDENTITY_INSERT </c> 之後是資料表）只換掉關鍵字，名稱照常。
    /// </remarks>
    public bool IsClosed { get; }

    /// <summary>
    /// 這一格除了 <see cref="Words"/> 也收變數：<c>SET </c> 之後是選項或 <c>@a = 1</c>。
    /// </summary>
    /// <remarks>
    /// 封閉的清單在空前綴就開好，打 <c>@</c> 只是篩選那一份；變數不在裡面的話，接在 <c>;</c>、
    /// <c>BEGIN</c> 之後的 <c>SET @</c> 什麼都列不出來，前一格判不出位置的 <c>SET @</c> 卻列得出來。
    /// 與封閉一樣由剖析器探測，<c>SET NOCOUNT </c> 不收。
    /// </remarks>
    public bool TakesVariable { get; }

    /// <summary>
    /// 這一格是名稱格：剖析器收名稱、不收值（<c>ALTER LOGIN </c>、<c>CREATE USER u FOR LOGIN </c>）。
    /// </summary>
    /// <remarks>
    /// 既有名稱那一格由它認，種類取尾巴的字，見 <see cref="Completion.SqlCatalogEntityPosition"/>。
    /// 收得了值的格子是運算式，名稱只是欄位的一種寫法，不算。
    /// </remarks>
    public bool TakesName { get; }

    /// <summary>
    /// 語句寫到片語為止已經完整（<c>CREATE INDEX i ON t (a) </c>、<c>OFFSET 10 ROWS </c>）。
    /// </summary>
    /// <remarks>
    /// 這種片語的字不含下一句的開頭（產生器扣掉了），所以換了行就只算可能：
    /// 換行之後的那一格可能是下一句，也可能還是這一句，字加進那一格的整份清單——<c>CREATE USER u</c>
    /// 換行之後還寫得出 <c>WITHOUT LOGIN</c>，<c>CREATE SEQUENCE s</c> 換行之後是 <c>START WITH</c>。
    /// 產生器手寫補回的字例外（<c>OFFSET 10 ROWS </c> 的 FETCH）：清單照樣略過片語，那個字仍在
    /// 語句開頭的清單裡；但寫出來之後它屬於這一句，見 <see cref="SqlClausePhraseCatalog.Continues"/>。
    /// </remarks>
    public bool EndsStatement { get; }

    /// <summary>
    /// 附加片語：只認位置，比對永遠是「可能」，只把關鍵字目錄給不了的字加進那一格。
    /// </summary>
    /// <remarks>
    /// 帶 <see cref="After"/> 的片語第一個字前面那段是位置本身；那個字不是目錄在那一格的關鍵字
    /// （<c>SELECT a AT TIME ZONE</c> 的 AT、語句開頭的 ENABLE）時由它給。比對確定的話整份目錄
    /// 讓給片語，選取清單尾端只剩 AT，所以它不換掉任何字。
    /// 帶尾巴的附加片語照尾巴比對，同樣只加字：剖析器分不出字的那一格（權限名稱，GRANT 什麼保留字都收）
    /// 與寫完一句卻還接著下一句的那一格（模組本體的 AS，空本體也剖析得過）。
    /// </remarks>
    public bool IsAdditive { get; }

    /// <summary>接得上的字，依產生器的順序。</summary>
    /// <remarks>
    /// 一項可以是幾個字：後面只接得了一個字的字與那個字併成一項（<c>ASYMMETRIC KEY</c>、
    /// <c>ENCRYPTION BY PASSWORD</c>），選一次寫完。這一格接得上的是第一個字，見 <see cref="Offers"/>。
    /// </remarks>
    public IReadOnlyList<string> Words { get; }

    /// <summary>這些字的建議項；說明是目前介面語言的。</summary>
    public IReadOnlyList<SqlSuggestion> Suggestions => _suggestions.Current;

    /// <summary>
    /// 片語第一個字前面那一格必須是這些位置；<see cref="SqlKeywordPosition.Any"/> 表示不看。
    /// </summary>
    /// <remarks>
    /// 同一條尾巴在不同位置是不同的意思：一句開頭的 <c>SET</c> 接工作階段選項，
    /// <c>UPDATE t SET</c> 接資料行；查詢寫完的 <c>FOR</c> 接 <c>XML</c>，資料表之後還多一個
    /// <c>SYSTEM_TIME</c>。產生器以這些位置的樣板探測，執行期以同一個位置分析回驗。
    /// </remarks>
    internal SqlKeywordPosition After { get; }

    /// <summary>產生器探測用的文字；測試拿它回驗比對。</summary>
    internal string Probe { get; }

    /// <summary>前一格對得上時的比對結果。</summary>
    internal SqlClausePhraseMatch Certain { get; }

    /// <summary>前一格判不出位置時的比對結果。</summary>
    internal SqlClausePhraseMatch Tentative { get; }

    /// <summary>
    /// 清單片語：最後一項是 <c>,*</c>，游標在標頭開的選項清單裡、逗號之後。
    /// </summary>
    /// <remarks>
    /// 尾巴從游標往回比對不到：中間夾著幾項已寫完的選項。清單由位置分析走訪（<see cref="SqlKeywordPosition.OptionItem"/>），
    /// 它交出錨點，這裡只比對錨點之前的標頭，見 <see cref="MatchHead"/>。
    /// </remarks>
    internal bool IsList => _elements.Length > 0 && _elements[_elements.Length - 1].Kind == ElementKind.List;

    /// <summary>片語最後一項是字面值（字、等號或逗號）時的那個字；比對前先用它分桶。</summary>
    internal string? LastWord =>
        _elements.Length > 0 && _elements[_elements.Length - 1] is { Kind: ElementKind.Word } last ? last.Word : null;

    /// <summary>片語有幾項；同時比對得上時，項數多的比較靠近游標的意思。</summary>
    /// <remarks>
    /// 零項的片語沒有尾巴，只認游標處的位置（<see cref="After"/>）：「這個位置接得了這些字」。
    /// 游標選項這種會重複的格子（<c>CURSOR LOCAL FAST_FORWARD </c>）尾巴寫不出來，位置寫得出來。
    /// 中段的 <c>,*</c> 是零到多項，自己不算一項：<c>TO ,* {name}</c> 與 <c>TO SCHEMA</c> 一樣長，以字面字結尾的優先；
    /// <c>FETCH FROM {name}</c> 比 <c>FROM ,* {name}</c> 長。
    /// </remarks>
    internal int Length { get; }

    /// <summary><paramref name="word"/> 是不是這個片語接得上的字；多字的一項認它的第一個字。</summary>
    internal bool Offers(string word) => _wordSet.Contains(word);

    /// <summary>一項的第一個字：<c>ASYMMETRIC KEY</c> 在這一格接得上的是 <c>ASYMMETRIC</c>。</summary>
    internal static string FirstWord(string item)
    {
        var space = item.IndexOf(' ');
        return space < 0 ? item : item.Substring(0, space);
    }

    /// <summary>
    /// <paramref name="tokens"/> 前 <paramref name="count"/> 個詞元的尾端是不是這個片語；
    /// 是的話回傳片語第一個詞元的索引，否則 -1。
    /// </summary>
    /// <param name="analyzer">同一段詞元的位置分析；<c>...</c> 要問它這一句的動詞。</param>
    internal int MatchTail(IReadOnlyList<SqlToken> tokens, int count, SqlKeywordPositionAnalyzer analyzer)
    {
        return MatchBefore(tokens, count - 1, _elements.Length, analyzer);
    }

    /// <summary>
    /// 清單片語的標頭是不是以 <paramref name="anchor"/> 結尾；是的話回傳片語第一個詞元的索引，否則 -1。
    /// </summary>
    internal int MatchHead(IReadOnlyList<SqlToken> tokens, int anchor, SqlKeywordPositionAnalyzer analyzer)
    {
        return MatchBefore(tokens, anchor, _elements.Length - 1, analyzer);
    }

    /// <summary>前 <paramref name="end"/> 項是不是以 <paramref name="last"/> 結尾；是的話回傳第一個詞元的索引，否則 -1。</summary>
    private int MatchBefore(IReadOnlyList<SqlToken> tokens, int last, int end, SqlKeywordPositionAnalyzer analyzer)
    {
        var index = last;

        for (var element = end - 1; element >= 0; element--)
        {
            if (index < 0)
            {
                return -1;
            }

            if (_elements[element].Kind == ElementKind.Rest)
            {
                return MatchRest(tokens, index, element, analyzer);
            }

            if (_elements[element].Kind == ElementKind.Items)
            {
                return MatchItems(tokens, index, element, analyzer);
            }

            // 名稱格收保留字，但不收開始一句的字：WITH CHECK_POLICY = ON⏎CREATE 的 CREATE 是下一句，
            // 當成 ON {name} 的名稱的話，那一格列的是資料行定義的字，CREATE 之後的 LOGIN 就不見了。
            // 也不收正在宣告的變數：DECLARE @a 是變數的宣告，不是 DECLARE c 這種 ISO 游標的名稱，之後是型別。
            var before = _elements[element].Kind == ElementKind.Name &&
                (analyzer.IsStatementHead(index) || DeclaresVariable(tokens, index))
                    ? Element.Mismatch
                    : _elements[element].MatchBackward(tokens, index);

            // 前後兩項之間可以夾著寫完的觸發程序選項（ON DATABASE WITH ENCRYPTION AFTER）：片語不寫選項，跨過再比一次。
            if (before == Element.Mismatch && element < end - 1 && analyzer.SkipTriggerOptions(index) is var target && target != index)
            {
                before = _elements[element].MatchBackward(tokens, target);
            }

            if (before == Element.Mismatch)
            {
                return -1;
            }

            index = before;
        }

        return index + 1;
    }

    /// <summary>
    /// <c>...</c> 以 <paramref name="last"/> 結尾：它前面那幾項從這一句的動詞寫起，中間至少隔一個詞元。
    /// </summary>
    /// <remarks>
    /// 動詞之後、錨點之前那一段由敘述自己決定（<c>EXEC p @a = 1, @b = 2 WITH</c>、
    /// <c>BACKUP DATABASE d TO DISK = 'x' WITH</c>），尾巴寫不出來；片語只說動詞是哪一個，動詞由位置分析找，
    /// 走不出這一句。那一段的第一個詞元不能是關鍵字：動詞緊接關鍵字是另一種敘述
    /// （<c>EXECUTE AS USER = 'u' WITH NO REVERT</c>），要另寫自己的片語。
    /// </remarks>
    private int MatchRest(IReadOnlyList<SqlToken> tokens, int last, int rest, SqlKeywordPositionAnalyzer analyzer)
    {
        var verb = analyzer.FindVerb(last);

        if (verb < 0)
        {
            return -1;
        }

        for (var head = verb; head < last; head++)
        {
            if (MatchBefore(tokens, head, rest, analyzer) != verb)
            {
                continue;
            }

            var first = tokens[head + 1];

            return first.Kind == SqlTokenKind.Identifier && !first.IsQuoted && SqlKeywordCatalog.IsKeyword(first.Value)
                ? -1
                : verb;
        }

        return -1;
    }

    /// <summary>
    /// 中段的 <c>,*</c> 以 <paramref name="last"/> 結尾：它前面那幾項是標頭，標頭之後到這裡是零到多項寫完的清單項。
    /// </summary>
    /// <remarks>
    /// 檔案規格一組一組寫下去（<c>ADD FILE (…), (…), (</c>），SET 選項也可以一次寫幾個（<c>SET LANGUAGE 'x', DATEFORMAT</c>）：
    /// 尾巴寫不出前面有幾項。從 <paramref name="last"/> 往回，每一格先試標頭能不能在那裡收尾，不能就走過一個詞元；
    /// 清單項裡寫得出的詞元與清單片語的選項同一條規則（<see cref="SqlKeywordPositionAnalyzer.StartsClauseOfItsOwn"/>），
    /// 一整組括號跳過。走不出這一句：<c>WHERE a IN (</c> 往回碰到 WHERE 就停，不會比對到前一句的 ADD FILE。
    /// 也走不出查詢的子句（<see cref="SqlKeywordPositionAnalyzer.StartsQueryClause"/>）：<c>GROUP BY a ORDER BY a, </c> 的逗號是 ORDER BY 的。
    /// 緊接一整組括號的 WITH 是那一項自己的選項（可用性複本 <c>'a' WITH (…), 'b' WITH (</c>），不是 CTE 的開頭；
    /// 括號還沒關上的（<c>FROM (…) WITH (</c> 的游標那一組）照舊是界線。
    /// </remarks>
    private int MatchItems(IReadOnlyList<SqlToken> tokens, int last, int items, SqlKeywordPositionAnalyzer analyzer)
    {
        // 之後是字面字或名稱的話它開始一項，前面緊接標頭或逗號：GRANT SELECT ON EXTERNAL 的 EXTERNAL 是類別，
        // 不是 GRANT ,* EXTERNAL 的權限；FROM t LEFT 的 LEFT 不是 FROM ,* {name} 的主體。
        // 之後是值、括號或逗號的不必（TO DISK = 'x' 的值前面是等號）。
        if (_elements[items + 1] is { Kind: ElementKind.Word or ElementKind.Name } next &&
            (next.Kind == ElementKind.Name || IsWord(next.Word!)) && !tokens[last].IsPunctuation(","))
        {
            return MatchBefore(tokens, last, items, analyzer);
        }

        for (var index = last; index >= 0; index--)
        {
            var start = MatchBefore(tokens, index, items, analyzer);

            if (start >= 0)
            {
                return start;
            }

            if (tokens[index].IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return -1;
                }
            }
            else if (analyzer.StartsQueryClause(index))
            {
                return -1;
            }
            else if (!tokens[index].IsPunctuation(",") && analyzer.StartsClauseOfItsOwn(index) &&
                     !(index < last && tokens[index].IsKeyword("WITH") && tokens[index + 1].IsPunctuation("(")))
            {
                return -1;
            }
        }

        return -1;
    }

    private IReadOnlyList<SqlSuggestion> BuildSuggestions()
    {
        // 右側說明只寫種類，與目錄裡的關鍵字相同。Tag 指回片語：過濾靠它分辨
        // 「這是這個片語的字」，而不是比對顯示文字——READ 同時在關鍵字目錄與片語裡。
        var keywordKind = SqlKindText.Keyword;
        var suggestions = new SqlSuggestion[Words.Count];

        for (var index = 0; index < suggestions.Length; index++)
        {
            var word = Words[index];
            suggestions[index] = new SqlSuggestion(word, word, keywordKind, word, SuggestionKind.Keyword, tag: this);
        }

        return suggestions;
    }

    private static Element[] Parse(string pattern)
    {
        var parts = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var elements = new Element[parts.Length];

        if (Array.IndexOf(parts, "...") != Array.LastIndexOf(parts, "..."))
        {
            throw new FormatException($"Phrase '{pattern}': ... can appear only once.");
        }

        for (var index = 0; index < parts.Length; index++)
        {
            elements[index] = parts[index] switch
            {
                // 前後都要有字面字：前面是動詞，後面是錨點，兩頭都釘住才比對得出來。
                "..." when index > 0 && index < parts.Length - 1 && IsWord(parts[index - 1]) && IsWord(parts[index + 1]) =>
                    new Element(ElementKind.Rest),
                "..." => throw new FormatException($"Phrase '{pattern}': ... must sit between two words."),
                "{name}" => new Element(ElementKind.Name),
                "{value}" => new Element(ElementKind.Value),
                "()" => new Element(ElementKind.Group),
                // 後面還有項時，那幾項是清單裡某一項的開頭（WITH (* TYPE =），左括號或逗號之後都算。
                "(*" when index > 0 => new Element(ElementKind.OpenList),
                "(*" => throw new FormatException($"Phrase '{pattern}': (* must follow a head."),
                // 括號裡是一個子句（WITHIN GROUP (ORDER BY a, b)、WAITFOR (RECEIVE a, b …)）：只認緊接的左括號，
                // 逗號屬於子句；寫成 (* 的話子句裡的逗號也比對成左括號，選取清單裡只剩子句開頭的字。
                "(" when index > 0 => new Element(ElementKind.Word, "("),
                "(" => throw new FormatException($"Phrase '{pattern}': ( must follow a head."),
                "=" => new Element(ElementKind.Word, "="),
                // 逗號分隔的是同一句裡重複的一段（ADD EVENT a.b, ADD EVENT），不是括號清單的項。
                "," => new Element(ElementKind.Word, ","),
                // JSON_OBJECT 的鍵與值之間（'k': v）。
                ":" => new Element(ElementKind.Word, ":"),
                ",*" when index == parts.Length - 1 && index > 0 => new Element(ElementKind.List),
                // 中段的 ,*：標頭之後零到多項清單項，後面那一項是清單裡某一項的一部分（ADD FILE ,* (*）。
                ",*" when index > 0 && parts[index + 1] is not (",*" or "...") => new Element(ElementKind.Items),
                ",*" => throw new FormatException($"Phrase '{pattern}': ,* must follow a head."),
                _ when parts[index].IndexOfAny(new[] { '{', '(', ')', ',' }) >= 0 =>
                    throw new FormatException($"Phrase '{pattern}': unknown element {parts[index]}."),
                _ => new Element(ElementKind.Word, parts[index])
            };
        }

        return elements;
    }

    private static bool IsWord(string part) => char.IsLetter(part[0]) || part[0] == '_';

    private static bool DeclaresVariable(IReadOnlyList<SqlToken> tokens, int index) =>
        tokens[index].Kind == SqlTokenKind.Variable && SqlScriptVariableSuggestions.IsDeclarationSlot(tokens, index);

    private enum ElementKind
    {
        Word,
        Name,
        Value,
        Group,
        OpenList,
        List,
        Items,
        Rest
    }

    private readonly struct Element
    {
        public Element(ElementKind kind, string? word = null)
        {
            Kind = kind;
            Word = word;
        }

        public ElementKind Kind { get; }

        public string? Word { get; }

        /// <summary>比對不成立。不用 -1：那是「比對到指令碼開頭」，是成立的。</summary>
        public const int Mismatch = -2;

        /// <summary>
        /// 從 <paramref name="last"/> 往回比對這一項；成立時回傳這一項前面那個詞元的索引，
        /// 否則 <see cref="Mismatch"/>。
        /// </summary>
        public int MatchBackward(IReadOnlyList<SqlToken> tokens, int last)
        {
            if (last < 0)
            {
                return Mismatch;
            }

            var token = tokens[last];

            switch (Kind)
            {
                // 等號是選項的指派（ALGORITHM = AES_256），與字一樣是字面值。
                case ElementKind.Word when Word == "=":
                    return token.Kind == SqlTokenKind.Operator && token.Value == "=" ? last - 1 : Mismatch;

                case ElementKind.Word when Word is "," or ":" or "(":
                    return token.IsPunctuation(Word) ? last - 1 : Mismatch;

                case ElementKind.Word:
                    return token.IsKeyword(Word!) && !(last >= 1 && tokens[last - 1].IsPunctuation("."))
                        ? last - 1
                        : MatchTypeName(tokens, last, Word!);

                case ElementKind.Name:
                    // 保留字也收：ALTER INDEX ALL ON t、ALTER DATABASE CURRENT 在名稱那一格寫的就是
                    // 保留字，而片語前後的字面值已經把位置釘住了，不必靠名稱這一格再擋。
                    // 變數也收：BACKUP DATABASE @db TO 是維護指令碼的常態寫法，而名稱寫不成變數的
                    // 語句本來就不合法，放行不會讓別的位置比對錯。
                    // 安全性實體的類別也是名稱的一部分：ADD SIGNATURE TO OBJECT::p BY 的名稱格寫的是 OBJECT::p。
                    if (token.Kind == SqlTokenKind.Variable)
                    {
                        return last - 1;
                    }

                    if (token.Kind != SqlTokenKind.Identifier)
                    {
                        return Mismatch;
                    }

                    var name = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last);
                    var securable = SqlCatalogEntityPosition.ClassStartBefore(tokens, name - 1);
                    return (securable >= 0 ? securable : name) - 1;

                // 值的格子在文法上是運算式：FETCH NEXT @a - @b + 1 ROWS、JSON_OBJECT('k': Title NULL。
                case ElementKind.Value:
                    return SqlOperand.SkipBackward(tokens, last) is var start and >= 0 ? start - 1 : Mismatch;

                case ElementKind.Group:
                    return MatchGroup(tokens, last);

                // 清單項從游標往回比對不到，由 MatchHead 從錨點比對標頭。
                case ElementKind.List:
                    return Mismatch;

                // 要看這一句的動詞，由 MatchRest 比對；清單項由 MatchItems 走過。
                case ElementKind.Rest:
                case ElementKind.Items:
                    return Mismatch;

                default:
                    if (!token.IsPunctuation("(") && !token.IsPunctuation(","))
                    {
                        return Mismatch;
                    }

                    var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, last);
                    return open >= 0 ? open - 1 : Mismatch;
            }
        }

        /// <summary>
        /// 內建型別的字寫成 <c>[xml]</c>、<c>sys.xml</c> 也是同一個型別：片語裡的型別字（<c>XML (*</c>）照樣對得上。
        /// </summary>
        /// <remarks>其他結構描述的同名型別是使用者定義型別，不算。</remarks>
        private static int MatchTypeName(IReadOnlyList<SqlToken> tokens, int last, string word)
        {
            var token = tokens[last];

            if (token.Kind != SqlTokenKind.Identifier ||
                !string.Equals(token.Value, word, StringComparison.OrdinalIgnoreCase) ||
                !SqlDataTypeCatalog.IsBuiltIn(word))
            {
                return Mismatch;
            }

            if (last < 1 || !tokens[last - 1].IsPunctuation("."))
            {
                return last - 1;
            }

            return last >= 2 && tokens[last - 2].Kind == SqlTokenKind.Identifier &&
                string.Equals(tokens[last - 2].Value, "sys", StringComparison.OrdinalIgnoreCase)
                    ? last - 3
                    : Mismatch;
        }

        private static int MatchGroup(IReadOnlyList<SqlToken> tokens, int last)
        {
            if (!tokens[last].IsPunctuation(")"))
            {
                return Mismatch;
            }

            var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, last);
            return open >= 0 ? open - 1 : Mismatch;
        }
    }
}
