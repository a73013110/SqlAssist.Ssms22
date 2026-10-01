using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 判斷游標落在 <see cref="SqlKeywordPosition"/> 的哪一個位置。
/// </summary>
/// <remarks>
/// 與 <c>tools/Generate-Keywords.ps1</c> 的樣板是一對的：產生器決定「哪些關鍵字
/// 可以出現在這個位置」，這裡決定「游標現在在哪個位置」。兩邊的粒度必須一致，
/// 因此樣板一律切在前一個詞元之後，這裡也只看前一個詞元加上最近的子句關鍵字。
///
/// 「最近的子句關鍵字」不是往回數詞元就找得到的，往回的路上有兩個結構要認：
///
/// <list type="bullet">
/// <item><b>括號群組</b>是一個完整的運算元，要整組跳過。把它當成一般詞元往裡面走，
/// 撈到的是子查詢自己的子句——<c>FROM (… ON a = b) x</c> 會判成「JOIN 條件之後」，
/// 於是 WHERE 從清單裡消失。</item>
/// <item><b>逗號</b>代表清單再來一項，位置回到清單的<b>起點</b>而不是尾端。
/// 判成尾端的話 <c>SELECT a, </c> 之後列的是 FROM、INTO、ORDER，
/// 而 CASE、CONVERT 這些真的能寫在那裡的字反而不見。</item>
/// </list>
///
/// 判不出來時回傳 <see cref="SqlKeywordPosition.Any"/>。分不出位置的代價是清單多幾個字，
/// 猜錯位置的代價是使用者要的關鍵字消失——後者嚴重得多。
///
/// 「這一格是使用者自己取的名字」是另一個軸，放在 <see cref="SqlCaretPosition.Slot"/>，
/// 見 <see cref="AfterGroup"/>、<see cref="IntroducesAlias"/>、<see cref="IsUnaliasedItem"/>
/// 與 <see cref="TryResolveNewName"/>。
///
/// 往回找的每一條路都停在同一個地方：游標所在那一句的開頭，見 <see cref="IsStatementHead"/>。
/// 一次分析一個執行個體，記住已經判過的語句開頭，並帶著原文——隱含的語句界線要看換行。
/// </remarks>
public sealed partial class SqlKeywordPositionAnalyzer
{
    /// <summary>前一個詞元是這些關鍵字時，位置可以直接決定。</summary>
    private static readonly Dictionary<string, SqlKeywordPosition> AfterKeyword =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // GO 是批次分隔符，之後必然是新批次的開頭。
            ["GO"] = SqlKeywordPosition.StatementStart,

            ["SELECT"] = SqlKeywordPosition.SelectList,

            ["FROM"] = SqlKeywordPosition.DataSource,
            ["JOIN"] = SqlKeywordPosition.DataSource,
            ["INTO"] = SqlKeywordPosition.DataSource,
            ["UPDATE"] = SqlKeywordPosition.DataSource,
            ["APPLY"] = SqlKeywordPosition.DataSource,

            ["WHERE"] = SqlKeywordPosition.Predicate,
            ["ON"] = SqlKeywordPosition.Predicate,
            ["HAVING"] = SqlKeywordPosition.Predicate,
            ["WHEN"] = SqlKeywordPosition.Predicate,
            ["AND"] = SqlKeywordPosition.Predicate,
            ["OR"] = SqlKeywordPosition.Predicate,

            // NOT 與 ON 同一條理由：文法允許兩個位置就報兩個，不必挑一個猜。
            // WHERE NOT | 開的是一個述詞，而 x NOT | 是運算子的一半——IN、LIKE、
            // BETWEEN 都掛在 ExpressionTail，只給述詞起點的症狀是
            // a.Big5Code NOT | 之後打不出 IN。
            ["NOT"] = SqlKeywordPosition.Predicate | SqlKeywordPosition.ExpressionTail,

            ["ORDER"] = SqlKeywordPosition.ByAnchor,
            ["GROUP"] = SqlKeywordPosition.ByAnchor,

            ["CREATE"] = SqlKeywordPosition.DdlObject,
            ["ALTER"] = SqlKeywordPosition.DdlObject,
            ["DROP"] = SqlKeywordPosition.DdlObject,

            ["BEGIN"] = SqlKeywordPosition.BlockStart,
            ["SET"] = SqlKeywordPosition.SetTarget,
            ["INSERT"] = SqlKeywordPosition.InsertTarget
        };

    /// <summary>
    /// 前一個詞元是識別字或一整組括號時，往回找最近的子句關鍵字來決定位置。
    /// </summary>
    /// <remarks>
    /// <c>BY</c> 不在這裡：它要看再前面是 ORDER 還是 GROUP，單獨出現沒有意義。
    ///
    /// <c>ON</c> 給的是兩個位置的聯集，因為 JOIN 條件寫完之後同時是
    /// 「述詞的尾端」（還能接 AND、OR）與「資料來源的尾端」（還能接 WHERE、
    /// 另一個 JOIN、GROUP、ORDER）。只給 <see cref="SqlKeywordPosition.ExpressionTail"/>
    /// 的話 WHERE 永遠不會出現——那正是「INNER JOIN 的 ON 寫完之後打不出 WHERE」。
    /// 位置本來就是旗標，文法允許兩個就報兩個，不必挑一個猜。
    ///
    /// <c>SET</c> 是同一件事的第二次：<c>UPDATE t SET a = 1 </c> 之後接得了
    /// <c>WHERE</c>、<c>FROM</c>、<c>OUTPUT</c>、<c>OPTION</c>，那一整組字掛在
    /// <see cref="SqlKeywordPosition.UpdateSetTail"/>——只給述詞尾端的症狀就是
    /// <c>UPDATE</c> 寫到一半打不出 <c>WHERE</c>。SET 選項（<c>SET NOCOUNT </c>、
    /// <c>SET NOCOUNT ON </c>）不走這裡，見 <see cref="FindSetOptionPart"/>。
    ///
    /// <c>MERGE</c> 與 <c>USING</c> 的目標和來源與 FROM 的資料來源同一種東西：
    /// 寫完之後同樣接得了別名，接著是 USING、ON。
    ///
    /// ORDER BY 與 GROUP BY 以兩個字的形式出現：錨點其實是 <c>BY</c>，要看前一個字才分得出
    /// 是哪一個子句，見 <see cref="FindAnchorPosition"/>。
    ///
    /// <c>IF</c>、<c>WHILE</c> 的條件寫完之後是主體那一句的開頭，也還能接 <c>AND</c>、<c>OR</c>。
    /// 語句開頭只屬於條件那一層，限制見 <see cref="FindAnchorPosition"/>。
    /// </remarks>
    private static readonly Dictionary<string, SqlKeywordPosition> ClauseAnchors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SELECT"] = SqlKeywordPosition.SelectListTail,

            ["FROM"] = SqlKeywordPosition.TableSourceTail,
            ["JOIN"] = SqlKeywordPosition.TableSourceTail,
            ["INTO"] = SqlKeywordPosition.TableSourceTail,
            ["UPDATE"] = SqlKeywordPosition.TableSourceTail,
            ["APPLY"] = SqlKeywordPosition.TableSourceTail,
            ["MERGE"] = SqlKeywordPosition.TableSourceTail,
            ["USING"] = SqlKeywordPosition.TableSourceTail,

            ["ON"] = SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail,

            ["WHERE"] = SqlKeywordPosition.ExpressionTail,
            ["HAVING"] = SqlKeywordPosition.ExpressionTail,
            ["SET"] = SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.UpdateSetTail,

            [OrderBy] = SqlKeywordPosition.OrderByTail,
            [GroupBy] = SqlKeywordPosition.GroupByTail,

            ["IF"] = SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.StatementStart,
            ["WHILE"] = SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.StatementStart
        };

    /// <summary>
    /// 逗號之後回到子句的起點；<see cref="ClauseAnchors"/> 認得而這裡沒有的，
    /// 一律不猜。
    /// </summary>
    /// <remarks>
    /// 不直接沿用 <see cref="AfterKeyword"/>：那裡是「緊接在這個字後面」，這裡是
    /// 「這一串清單的下一項」，<c>UPDATE</c>、<c>INTO</c> 這些字只有前一種。
    ///
    /// <c>SET</c> 兩種都是同一格：<c>UPDATE t SET a = 1, </c> 與 <c>SET </c> 一樣是
    /// 指派清單的起點。資料行由 <c>SqlColumnOwner</c> 認出所屬的資料表，
    /// 這一格的關鍵字（<c>ROWCOUNT</c> 這些）在那裡被資料行目標換掉。
    /// </remarks>
    private static readonly Dictionary<string, SqlKeywordPosition> ListAnchors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SELECT"] = SqlKeywordPosition.SelectList,

            ["FROM"] = SqlKeywordPosition.DataSource,
            ["JOIN"] = SqlKeywordPosition.DataSource,
            ["INTO"] = SqlKeywordPosition.DataSource,
            ["APPLY"] = SqlKeywordPosition.DataSource,

            ["WHERE"] = SqlKeywordPosition.Predicate,
            ["ON"] = SqlKeywordPosition.Predicate,
            ["HAVING"] = SqlKeywordPosition.Predicate,
            ["SET"] = SqlKeywordPosition.SetTarget,

            // 下一項仍然是欄位。
            [OrderBy] = SqlKeywordPosition.OrderByColumn,
            [GroupBy] = SqlKeywordPosition.OrderByColumn
        };

    /// <summary>ORDER BY 在錨點表裡的鍵。</summary>
    private const string OrderBy = "ORDER BY";

    /// <summary>GROUP BY 在錨點表裡的鍵。</summary>
    private const string GroupBy = "GROUP BY";

    /// <summary>括號直接接在這些字後面時，括號裡的查詢是一個資料來源。</summary>
    /// <remarks>
    /// <c>IN (SELECT …)</c>、<c>EXISTS (SELECT …)</c>、<c>= (SELECT …)</c> 的括號
    /// 裡面雖然也是查詢，但那是運算式，後面接的不是別名。
    ///
    /// <c>INTO</c> 不在這裡：<c>SELECT … INTO #t</c> 的目標不是衍生資料表。
    /// <c>USING</c> 在——MERGE 的來源與 FROM 的來源是同一條文法。
    /// </remarks>
    private static readonly HashSet<string> TableSourceKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "JOIN", "APPLY", "USING"
        };

    /// <summary>
    /// <c>CREATE</c> 之後寫完這幾個字，下一格是物件的名稱；字數多的在前，每一種帶著那一格是哪一種名字。
    /// </summary>
    /// <remarks>
    /// 不手寫：產生器從 <c>CREATE</c> 一路展開到名稱為止（<c>SYMMETRIC KEY</c>、<c>UNIQUE CLUSTERED INDEX</c>、
    /// <c>OR ALTER PROCEDURE</c>）。手寫的名單只收得到常用的十幾種，其餘種類的名稱格整份目錄全部進場。
    /// 那一格是什麼名字見 <see cref="CreatedNameSlot"/>。
    /// </remarks>
    private static readonly (string[] Words, bool MayExist, bool SchemaQualified)[] CreatedKinds = BuildCreatedKinds();

    /// <summary>標頭以 <c>AS</c> 結束、後面接主體的物件種類。</summary>
    private static readonly HashSet<string> ModuleKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "VIEW"
        };

    /// <summary>語句可以從這些位置開始：語句開頭、區塊開頭與區塊的 END 之後。</summary>
    private const SqlKeywordPosition StatementBoundaries =
        SqlKeywordPosition.StatementStart | SqlKeywordPosition.BlockStart | SqlKeywordPosition.BlockEnd;

    /// <summary>判斷語句開頭時，往前一句再問一次前一格的層數上限。</summary>
    /// <remarks>
    /// 一連串沒有子句關鍵字的敘述（幾千行的 <c>EXEC</c>、<c>DECLARE</c>）會一句接一句往前問；
    /// 不設上限的話堆疊深度跟著指令碼長度走。超過時前一格當成判不出來（<see cref="SqlKeywordPosition.Any"/>），
    /// 那一句於是可能在這裡結束——語句切短了，清單多幾個字，不會少。
    /// </remarks>
    private const int MaxNesting = 16;

    private readonly IReadOnlyList<SqlToken> tokens;
    private readonly string textBeforeToken;

    /// <summary>判過的語句開頭；同一個詞元會被好幾條往回的路問到。</summary>
    private readonly Dictionary<int, bool> heads = new();

    private int nesting;

    /// <summary>每個位置還沒關上的左括號，第一次問才算，見 <see cref="FindUnclosedParenthesis"/>。</summary>
    private int[]? unclosedParentheses;

    private SqlKeywordPositionAnalyzer(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
    {
        this.tokens = tokens;
        this.textBeforeToken = textBeforeToken;
    }

    /// <summary>
    /// 問整份指令碼的語句界線與 FROM 的歸屬，見 <see cref="SqlStatementBoundaries"/>。
    /// </summary>
    /// <remarks>
    /// 這兩個問題只往回看，所以 <paramref name="text"/> 可以是整份指令碼：
    /// 詞元之後的文字不影響前面的答案。
    /// </remarks>
    internal static SqlKeywordPositionAnalyzer ForScript(IReadOnlyList<SqlToken> tokens, string text) =>
        new(tokens, text);

    /// <summary>
    /// 分析游標所在的位置。
    /// </summary>
    /// <param name="textBeforeToken">
    /// 游標前方的文字，且不含正在輸入的那個詞元——位置由「前一個完整的詞元」決定，
    /// 打到一半的字不算數。
    /// </param>
    /// <returns>
    /// 文法允許的位置，以及這一格是不是使用者自己取的名字。
    /// </returns>
    public static SqlCaretPosition Analyze(string textBeforeToken)
    {
        if (textBeforeToken is null)
        {
            throw new ArgumentNullException(nameof(textBeforeToken));
        }

        return Analyze(SqlTokenizer.Tokenize(textBeforeToken), textBeforeToken);
    }

    /// <summary>
    /// 同上，但由呼叫端交出已經分析好的詞元。
    /// </summary>
    /// <remarks>
    /// 上下文分析同一段文字還要問別的問題（例如「這裡是不是型別的位置」），
    /// 各自再分析一次的話，每按一鍵就把游標前的整份指令碼掃兩遍。
    /// <paramref name="textBeforeToken"/> 仍然要傳：換行的位置只有原文有。
    /// </remarks>
    public static SqlCaretPosition Analyze(
        IReadOnlyList<SqlToken> tokens,
        string textBeforeToken)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (textBeforeToken is null)
        {
            throw new ArgumentNullException(nameof(textBeforeToken));
        }

        var analyzer = new SqlKeywordPositionAnalyzer(tokens, textBeforeToken);
        var caret = analyzer.AnalyzeClause();
        var listAnchor = caret.Keywords != SqlKeywordPosition.Any &&
            (caret.Keywords & SqlKeywordPosition.OptionItem) != SqlKeywordPosition.None
                ? analyzer.FindPhraseListAnchor(tokens.Count - 1)
                : -1;
        var phrase = SqlClausePhraseCatalog.Match(analyzer, tokens, textBeforeToken, caret.Keywords, listAnchor);

        return new SqlCaretPosition(caret.Keywords, caret.Slot, phrase, analyzer.StartsBatch(), caret.NewNameQualifier);
    }

    /// <summary>同一個批次裡游標前面還沒有任何詞元。</summary>
    /// <remarks>
    /// 批次的開頭是語句界線的一種，只是更強：省略 EXEC 的程序呼叫只在這裡合法。
    /// 詞法分析只把 ScriptDom 判定的批次分隔回報成 <c>GO</c> 關鍵字，名為 GO 的欄位不算。
    /// </remarks>
    private bool StartsBatch()
    {
        return tokens.Count == 0 || tokens[tokens.Count - 1].IsKeyword("GO");
    }

    /// <summary>
    /// <paramref name="index"/> 的詞元前面那一格是什麼位置。
    /// </summary>
    /// <remarks>
    /// 子句片語的 <see cref="SqlClausePhrase.After"/> 問的就是這個：<c>UPDATE t SET </c> 的 SET
    /// 前面是資料來源尾端，接的是資料行，不是工作階段選項。判法與游標處的位置分析同一條，
    /// 子句寫完又換了行時一樣補上語句開頭——但只在這個字真的開始一句時（<see cref="IsStatementHead"/>）。
    /// </remarks>
    internal static SqlKeywordPosition PositionBefore(IReadOnlyList<SqlToken> tokens, int index, string textBeforeToken)
    {
        return index <= 0
            ? SqlKeywordPosition.StatementStart
            : new SqlKeywordPositionAnalyzer(tokens, textBeforeToken).PositionBefore(index);
    }

    /// <summary>同上，由這個分析器回答；語句開頭的判準問片語時走這裡，判過的開頭不必重算。</summary>
    internal SqlKeywordPosition PositionBefore(int index)
    {
        if (index <= 0)
        {
            return SqlKeywordPosition.StatementStart;
        }

        var before = KeywordsBefore(index);
        var position = AddStatementEnd(before, index - 1, tokens[index].Start);

        // 換行只說下一句「可能」從這裡開始；這個字自己是不是開頭，問語句開頭的判準。
        // UPDATE t⏎SET 的 SET 是資料行指派，當成開頭的話片語會列出 NOCOUNT 這些工作階段選項。
        return (before & SqlKeywordPosition.StatementStart) == SqlKeywordPosition.None &&
            !IsStatementHead(index)
                ? position & ~SqlKeywordPosition.StatementStart
                : position;
    }

    /// <summary><paramref name="index"/> 的詞元前面那一格的位置，不含換行補上的語句開頭。</summary>
    /// <remarks>
    /// 往前一句再問一次的入口都走這裡，層數由 <see cref="MaxNesting"/> 擋住。
    /// </remarks>
    private SqlKeywordPosition KeywordsBefore(int index)
    {
        if (index <= 0)
        {
            return SqlKeywordPosition.StatementStart;
        }

        if (nesting >= MaxNesting)
        {
            return SqlKeywordPosition.Any;
        }

        nesting++;

        try
        {
            return AnalyzeAt(index - 1, followAlias: true).Keywords;
        }
        finally
        {
            nesting--;
        }
    }

    private SqlCaretPosition AnalyzeClause()
    {
        var last = tokens.Count - 1;
        var caret = AnalyzeAt(last, followAlias: true);

        // 名字那一格前面的換行不代表下一句開始了：名字本身還沒寫。
        if (caret.Slot != SqlCompletionSlot.Grammar)
        {
            return caret;
        }

        // 沒有 AS 的別名可能是名字。這一支放在最後而不是併進 AnalyzeAt：
        // 它要看的是原文裡的換行，而詞元串流沒有那個資訊。別名寫完之後接的位置
        // 與不寫別名時一樣（選取清單尾端、資料來源尾端），所以關鍵字位置原樣留著——
        // SELECT PublCode FR 仍然列得出 FROM。
        var keywords = AddStatementEnd(caret.Keywords, last, textBeforeToken.Length);

        return StaysOnSameLine() && IsUnaliasedItem(last, caret.Keywords)
            ? new SqlCaretPosition(keywords, SqlCompletionSlot.MaybeName)
            : new SqlCaretPosition(keywords);
    }

    /// <summary>這些位置又換了行時，這裡同時也可能是下一個敘述的開頭。</summary>
    /// <remarks>
    /// 子句尾端之外還有 SET 選項名稱之後：識別字的選項值（<c>SET DATEFORMAT dmy</c>）
    /// 在詞元上與名稱分不開，見 <see cref="FindSetOptionPart"/>。
    /// </remarks>
    private const SqlKeywordPosition StatementEndPositions =
        SqlKeywordPosition.SelectListTail |
        SqlKeywordPosition.TableSourceTail |
        SqlKeywordPosition.ExpressionTail |
        SqlKeywordPosition.OrderByTail |
        SqlKeywordPosition.GroupByTail |
        SqlKeywordPosition.SelectIntoTail |
        SqlKeywordPosition.FetchTail |
        SqlKeywordPosition.UpdateSetTail |
        SqlKeywordPosition.SetOptionValue;

    /// <summary>
    /// 子句寫完又換了行時，把語句開頭補進位置裡。
    /// </summary>
    /// <remarks>
    /// T-SQL 的分號是選用的，所以敘述的結尾沒有任何詞元標示得出來：
    /// <c>WHERE a = 1</c> 之後換行寫 <c>SELECT</c> 與換行寫 <c>AND</c>，在詞元串流上
    /// 完全一樣。少了這一條的症狀是使用者不打分號時，下一句的所有語句級片段
    /// （<c>ssf</c>…）在清單裡一個都沒有，而打了分號就有——他看不出兩者的差別，
    /// 只會覺得片段時有時無。
    ///
    /// 補的是位元而不是換掉：位置本來就是旗標，<c>AND</c>、<c>OR</c>、<c>ORDER</c>
    /// 這些續寫子句的字一個都不能少。猜錯敘述邊界的代價必須是清單多幾個字，
    /// 不能是少幾個字。
    ///
    /// 選取清單尾端也可能結束敘述：<c>SELECT dbo.fn_Fee('')</c> 不需要 FROM。
    /// 舊版為了減少候選而排除它，導致函式、常數與變數查詢後都必須補分號才能打片段。
    /// 不依函式名稱特判，也不放行成 Any；保留尾端旗標，讓 FROM 與下一句同時可選。
    ///
    /// 換行是唯一的線索，理由與 <see cref="StaysOnSameLine"/> 相同，只是方向相反：
    /// 同一行代表他還在寫同一個子句。
    ///
    /// 這也是語句開頭的判準之一（隱含的界線），見 <see cref="IsStatementHead"/>。
    /// </remarks>
    /// <param name="position">換行之前那一格的位置。</param>
    /// <param name="last">子句最後一個詞元。</param>
    /// <param name="gapEnd">換行要落在 <paramref name="last"/> 之後、這個位置之前。</param>
    private SqlKeywordPosition AddStatementStartOnNewLine(SqlKeywordPosition position, int last, int gapEnd)
    {
        if ((position & SqlKeywordPosition.StatementStart) != SqlKeywordPosition.None ||
            (position & StatementEndPositions) == SqlKeywordPosition.None ||
            last < 0 ||
            !StartsOnNewLine(tokens[last].End, gapEnd, textBeforeToken))
        {
            return position;
        }

        // 函式引數、子查詢與 CTE 還在括號內時，換行不代表可以開始獨立敘述。
        return FindUnclosedParenthesis(last) < 0
            ? position | SqlKeywordPosition.StatementStart
            : position;
    }

    /// <summary>
    /// <paramref name="from"/> 以前還沒關上的左括號；沒有就是 -1。答案與
    /// <see cref="SqlTokenNavigator.FindUnclosedParenthesis"/> 相同。
    /// </summary>
    /// <remarks>
    /// 語句開頭的判準每一次都要問，而往回找一次最壞要走到指令碼開頭：整份指令碼的範圍分析
    /// 會問遍每一句的開頭，那就是平方。一趟算完所有位置之後每次都是查表。
    /// </remarks>
    private int FindUnclosedParenthesis(int from)
    {
        if (from < 0 || tokens.Count == 0)
        {
            return -1;
        }

        unclosedParentheses ??= SqlTokenNavigator.MapUnclosedParentheses(tokens);
        return unclosedParentheses[Math.Min(from, tokens.Count - 1)];
    }

    /// <summary><paramref name="previousTokenEnd"/> 到 <paramref name="gapEnd"/> 之間隔了至少一個換行。</summary>
    internal static bool StartsOnNewLine(int previousTokenEnd, int gapEnd, string textBeforeToken)
    {
        // 詞法分析已略過註解；直接查看詞元後的間隙，避免區塊註解遮住換行，
        // 也不會把字串或加引號名稱內的換行誤認成敘述邊界。
        for (var index = previousTokenEnd; index < gapEnd; index++)
        {
            var character = textBeforeToken[index];

            if (character is '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 子句寫完之後，這一句可能就此結束：換了行補上語句開頭，IF 只有一句的主體補上 ELSE。
    /// </summary>
    private SqlKeywordPosition AddStatementEnd(SqlKeywordPosition position, int last, int gapEnd)
    {
        position = AddStatementStartOnNewLine(position, last, gapEnd);

        return EndsIfBody(position, last) ? position | SqlKeywordPosition.IfBodyEnd : position;
    }

    /// <summary>
    /// 游標前那一句可能已經寫完，而它是 IF 只有一句的主體：<c>IF @a = 1 SELECT 1 </c>。
    /// </summary>
    /// <remarks>
    /// 主體從 IF 的條件寫完之後的語句開頭開始，所以問的是：這一句的開頭前面那個詞元，
    /// 所在的那一句是不是以 IF 開頭。已經有主體的 IF（<c>IF a SELECT 1 SELECT 2 </c>）、
    /// ELSE 之後那一句與 WHILE 的主體都不是。
    ///
    /// 這一句還停在自己的條件裡（<c>IF a IF b = 1 </c>）時不算，ELSE、<c>BEGIN TRY</c>
    /// 這種打開下一句的邊界之後也還沒有寫完的一句。主體是 <c>BEGIN … END</c> 時由
    /// <see cref="SqlKeywordPosition.BlockEnd"/> 給 ELSE。
    /// </remarks>
    private bool EndsIfBody(SqlKeywordPosition position, int last)
    {
        if (last < 0 ||
            position == SqlKeywordPosition.Any ||
            (position & (StatementEndPositions | SqlKeywordPosition.StatementStart)) == SqlKeywordPosition.None ||
            FindBlockBoundary(last) == SqlKeywordPosition.StatementStart)
        {
            return false;
        }

        var head = FindStatementStart(last);

        if (head < 1 || head > last || tokens[head].IsKeyword("IF") || tokens[head].IsKeyword("WHILE"))
        {
            return false;
        }

        var condition = FindStatementStart(head - 1);

        return condition < head && tokens[condition].IsKeyword("IF");
    }

    /// <summary>
    /// <paramref name="from"/> 所在的那一句從哪個詞元開始。
    /// </summary>
    /// <remarks>
    /// 分號與 GO 之後、語句開頭（<see cref="IsStatementHead"/>）都是界線；
    /// <paramref name="from"/> 本身是分號或 GO 時回傳它的下一個，那一句還是空的。
    /// 括號整組跳過，沒關上的左括號照樣穿過去，理由與 <see cref="FindAnchorPosition"/> 相同。
    /// </remarks>
    private int FindStatementStart(int from)
    {
        for (var index = from; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (open < 0)
                {
                    return index + 1;
                }

                index = open;
                continue;
            }

            if (token.IsPunctuation(";") || token.IsKeyword("GO"))
            {
                return index + 1;
            }

            if (IsStatementHead(index))
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>
    /// <paramref name="index"/> 是一句的開頭：能開始一句的關鍵字，而且前一格是一句的界線。
    /// </summary>
    /// <remarks>
    /// 能開始一句的字也寫在一句的中間：<c>WITH (NOLOCK)</c>、<c>DROP TABLE IF EXISTS</c>、
    /// <c>INSERT … SELECT</c>、MERGE 的 <c>THEN UPDATE</c>。分得開它們的是前一格，界線有兩種：
    ///
    /// <list type="bullet">
    /// <item><b>明確的</b>：前一格的位置含語句開頭、區塊開頭或區塊的 END——分號、GO、BEGIN、
    /// ELSE、模組標頭的 AS、IF 的條件、SET 選項的值、寫完一整句的字（<c>BREAK</c>）之後。</item>
    /// <item><b>隱含的</b>：T-SQL 的分號是選用的。子句寫完又換了行
    /// （<see cref="AddStatementStartOnNewLine"/>），或前一句沒有子句關鍵字、判不出位置，
    /// 而它寫到一個運算元或寫完一整句的字（<see cref="MayEndStatement"/>）。</item>
    /// </list>
    ///
    /// 隱含的界線只是猜測，剖析器不看換行：前一句寫到這裡比對到的子句片語接得上這個字時，
    /// 它是前一句的下一段（<see cref="SqlClausePhraseCatalog.Continues"/>）。<c>OFFSET 0 ROWS⏎FETCH</c>
    /// 的 FETCH 接的是 NEXT 5 ROWS ONLY，<c>ALTER DATABASE d⏎SET</c> 的 SET 是資料庫選項；
    /// 當成開頭的話，語句說明開出資料指標的 FETCH，範圍分析把後半句切成下一句。
    ///
    /// 前一格判不出位置、前一個詞元又還沒寫完（<c>DROP TABLE |IF</c>、<c>THEN |UPDATE</c>、
    /// <c>FOR |SELECT</c>）時不是開頭：那裡的字屬於同一句。
    ///
    /// <c>WITH</c> 只認明確的界線：CTE 前一句必須以分號結束（SQL Server 錯誤 319），
    /// 而同一個字在 <c>CREATE VIEW v⏎WITH SCHEMABINDING</c>、<c>EXEC p WITH RECOMPILE</c>
    /// 裡接在隱含的界線後面，卻是那一句的選項。
    ///
    /// <c>SET</c> 接在隱含的界線後面時還要問它所屬的動詞：<c>UPDATE t⏎SET</c> 是 UPDATE 的子句
    /// （<see cref="IntroducesOptions"/>）。當成一句的開頭的話，範圍分析把 UPDATE 的目標切掉，
    /// IF 只有一句的主體也找錯開頭。
    /// </remarks>
    internal bool IsStatementHead(int index)
    {
        var token = tokens[index];

        if (token.Kind != SqlTokenKind.Identifier || !IsBareKeyword(index) || !StartsStatement(token))
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        if (heads.TryGetValue(index, out var known))
        {
            return known;
        }

        var before = KeywordsBefore(index);
        bool head;

        if (before != SqlKeywordPosition.Any && (before & StatementBoundaries) != SqlKeywordPosition.None)
        {
            head = true;
        }
        else if (token.IsKeyword("WITH"))
        {
            head = false;
        }
        else
        {
            head = before == SqlKeywordPosition.Any
                ? MayEndStatement(index - 1)
                : (AddStatementStartOnNewLine(before, index - 1, token.Start) & SqlKeywordPosition.StatementStart)
                    != SqlKeywordPosition.None;

            // 隱含的界線是猜的，剖析器不猜：前一句寫到這裡接得上這個字，它就還是那一句。
            head = head && !SqlClausePhraseCatalog.Continues(tokens, index, before, this, token.Value);
        }

        if (head && token.IsKeyword("SET") && !IntroducesOptions(index))
        {
            head = false;
        }

        heads[index] = head;
        return head;
    }

    /// <summary>這個關鍵字能開始一句。</summary>
    private static bool StartsStatement(SqlToken keyword) => SqlKeywordCatalog.StartsStatement(keyword.Value);

    /// <summary>
    /// 沒有子句關鍵字的一句（<c>EXEC</c>、<c>PRINT</c>、<c>DECLARE</c>）可以在 <paramref name="index"/> 結束。
    /// </summary>
    /// <remarks>
    /// 一個運算元寫完：名稱、變數、常值、右括號、<c>NULL</c> 這種自成一項的字；或這個字本身寫完一整句
    /// （<c>COMMIT</c>、<c>RETURN</c>）。其餘的關鍵字、運算子與逗號之後那一句還沒寫完。
    /// </remarks>
    private bool MayEndStatement(int index)
    {
        var token = tokens[index];

        return token.Kind switch
        {
            SqlTokenKind.Identifier => !IsBareKeyword(index) ||
                SqlKeywordCatalog.EndsItem(token.Value) ||
                SqlKeywordCatalog.EndsStatement(token.Value),
            SqlTokenKind.Punctuation => token.IsPunctuation(")"),
            SqlTokenKind.Operator => false,
            _ => true
        };
    }

    /// <summary>
    /// <paramref name="last"/> 這個字寫完一整句，而且那一句再也接不了語句開頭以外的東西：
    /// <c>BREAK</c>、<c>CHECKPOINT</c>。
    /// </summary>
    /// <remarks>
    /// 哪些字、前一格在哪些位置時算，由產生器判定，見 <see cref="SqlKeywordCatalog.ClosesStatement"/>。
    /// 區塊開頭與區塊的 END 之後也是一句的開頭，前一格落在那裡時照語句開頭查。
    /// </remarks>
    private bool ClosesStatement(int last)
    {
        var keyword = tokens[last].Value;

        if (!SqlKeywordCatalog.EndsStatement(keyword))
        {
            return false;
        }

        var before = KeywordsBefore(last);

        if (before != SqlKeywordPosition.Any && (before & StatementBoundaries) != SqlKeywordPosition.None)
        {
            before |= SqlKeywordPosition.StatementStart;
        }

        return SqlKeywordCatalog.ClosesStatement(keyword, before);
    }

    /// <summary>
    /// 往回第一個能開始一句的字：<paramref name="from"/> 所在的子句屬於哪一個動詞；沒有就是 -1。
    /// </summary>
    /// <remarks>
    /// 動詞不一定是這一句的開頭：<c>INSERT … SELECT</c> 的 SELECT、MERGE 的 <c>THEN UPDATE</c>、
    /// <c>UPDATE t SET</c> 的 SET 都帶著自己的子句，所以問的是能不能開始一句，不問
    /// <see cref="IsStatementHead"/>。它也走不出這一句：這一句的開頭本身就是這種字。
    ///
    /// 能開始一句卻帶不出子句的字不是動詞：<c>WITH</c>（CTE 後面還有自己的動詞，其餘是提示、
    /// 選項與 <c>WITH TIES</c>），以及權限清單的一項（<c>REVOKE SELECT, INSERT ON t FROM u</c>，
    /// 見 <see cref="NamesPermission"/>）。
    ///
    /// 一組括號是一個單位，前面的函式名稱也算在裡面：<c>TOP (5)</c>、<c>IF UPDATE(a)</c> 的 UPDATE
    /// 是函式。其餘能開始一句的字接括號時本身就是動詞：<c>SELECT (a + b) AS x FROM</c>、
    /// <c>RAISERROR (…)</c>、<c>THEN INSERT (a)</c>。分號、沒關上的左括號與配不起來的
    /// 右括號之前是別的東西。
    /// </remarks>
    internal int FindVerb(int from)
    {
        for (var index = from; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (open < 0)
                {
                    return -1;
                }

                index = open > 0 && IsFunctionName(open - 1) ? open - 1 : open;
                continue;
            }

            if (token.IsPunctuation(";") || token.IsPunctuation("("))
            {
                return -1;
            }

            // 寫完的 CASE … END 是一個運算元，與括號整組跳過同理：它的 END、ELSE 能開始一句，
            // 停在那裡的話 SELECT CASE … END FROM t a 的 FROM 找不到 SELECT，a 就不算資料來源。
            if (token.IsKeyword("END") && FindCaseStart(index) is var caseStart and >= 0)
            {
                index = caseStart;
                continue;
            }

            if (IsVerbCandidate(index) && !token.IsKeyword("WITH") && !NamesPermission(index))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>沒加引號、不在點號後面、而且能開始一句的關鍵字。</summary>
    private bool IsVerbCandidate(int index) =>
        tokens[index].Kind == SqlTokenKind.Identifier && IsBareKeyword(index) && StartsStatement(tokens[index]);

    /// <summary>緊接在左括號前面的 <paramref name="name"/> 與括號同一個單位：函式名稱，或也是函式的 UPDATE。</summary>
    private bool IsFunctionName(int name) =>
        tokens[name].Kind == SqlTokenKind.Identifier && (!IsVerbCandidate(name) || tokens[name].IsKeyword("UPDATE"));

    /// <summary>
    /// <paramref name="keyword"/> 的 FROM 或 INTO 後面接資料來源，由它所屬的動詞（<see cref="FindVerb"/>）決定：
    /// FROM 要 SELECT、UPDATE 或 DELETE，INTO 只有 FETCH 的不接。
    /// </summary>
    /// <remarks>
    /// 同一個字在別的動詞底下接的是別的東西：<c>FETCH NEXT FROM</c> 的游標、
    /// <c>RESTORE DATABASE d FROM DISK</c> 的備份裝置、<c>REVOKE … FROM</c> 的主體、
    /// <c>BULK INSERT t FROM</c> 的檔案、<c>CREATE LOGIN l FROM WINDOWS</c>。當成資料來源的症狀是
    /// <c>DISK</c> 被收成一張表，每次按鍵多查一次不存在的名稱，而 FROM 之後的清單全是資料表。
    ///
    /// INTO 反過來只有一個例外：SELECT … INTO、INSERT、MERGE 與 OUTPUT … INTO 接的都是資料表，
    /// <c>FETCH NEXT FROM c INTO</c> 接的是變數清單。當成資料來源的症狀是那裡整份列成資料表，
    /// 打 <c>@</c> 之後資料表變數也照資料來源提交。
    ///
    /// UPDATE 的 FROM 所屬的動詞是 SET：帶資料行指派的 SET（<see cref="IntroducesOptions"/>）算 UPDATE。
    /// 判不出動詞時照舊當成資料來源：<c>TRIM(' ' FROM x)</c> 那種在括號裡，範圍分析本來就不讀。
    ///
    /// 位置分析（FROM、INTO 之後，以它們為錨點的清單與子句尾端）、上下文分析的目標與範圍分析的資料來源
    /// 都問這一條。
    /// </remarks>
    internal bool IntroducesDataSource(int keyword)
    {
        var verb = FindVerb(keyword - 1);

        if (verb < 0)
        {
            return true;
        }

        var token = tokens[verb];

        if (tokens[keyword].IsKeyword("INTO"))
        {
            return !token.IsKeyword("FETCH");
        }

        return token.IsKeyword("SELECT") ||
            token.IsKeyword("UPDATE") ||
            token.IsKeyword("DELETE") ||
            (token.IsKeyword("SET") && !IntroducesOptions(verb));
    }

    /// <summary>
    /// <paramref name="index"/> 這個詞元之後是游標名稱：<c>OPEN</c>、<c>CLOSE</c>、<c>DEALLOCATE</c>、
    /// 游標語句的 <c>FETCH</c>、<c>FETCH … FROM</c>、<c>WHERE CURRENT OF</c>，以及接在它們後面的 <c>GLOBAL</c>。
    /// </summary>
    /// <remarks>
    /// <c>GLOBAL</c> 不是關鍵字，當成名稱的話 <c>FETCH NEXT FROM GLOBAL </c> 就是游標已經寫完、只剩 <c>INTO</c>。
    /// 它後面再一個 <c>GLOBAL</c> 才是名稱。<c>OFFSET 0 ROWS FETCH</c> 的 FETCH 不是一句的開頭，不算。
    ///
    /// 位置分析（<c>FETCH NEXT FROM c </c> 的尾端）與上下文分析的目標都問這一條。
    /// </remarks>
    internal bool IntroducesCursor(int index)
    {
        // 不看關鍵字目錄：GLOBAL 不在裡面。加引號的由 IsKeyword 擋掉，點號後面的是名稱的一段。
        if (index < 0 || (index >= 1 && tokens[index - 1].IsPunctuation(".")))
        {
            return false;
        }

        var token = tokens[index];

        if (token.IsKeyword("GLOBAL"))
        {
            return index >= 1 && !tokens[index - 1].IsKeyword("GLOBAL") && IntroducesCursor(index - 1);
        }

        return token.IsKeyword("OPEN") ||
            token.IsKeyword("CLOSE") ||
            token.IsKeyword("DEALLOCATE") ||
            (token.IsKeyword("FETCH") && IsStatementHead(index)) ||
            (token.IsKeyword("OF") && index >= 1 && tokens[index - 1].IsKeyword("CURRENT")) ||
            (token.IsKeyword("FROM") && FindVerb(index - 1) is var verb and >= 0 && tokens[verb].IsKeyword("FETCH"));
    }

    /// <summary>
    /// 游標與前一個詞元之間隔著東西，而且沒有換行。
    /// </summary>
    /// <remarks>
    /// 別名一定寫在同一行，子句與下一個敘述則幾乎總是換行寫——
    /// 這是唯一分得開「他在取別名」與「他在打 WHERE」的線索，因為兩者在文法上
    /// 都成立，而打到一半的 <c>WHE</c> 與別名在剖析器眼中一模一樣。
    ///
    /// 換行的判準與 <see cref="StartsOnNewLine"/> 是同一條，只是方向相反：看的是
    /// 前一個詞元結尾到游標之間的原文，所以區塊註解前面的換行一樣算數。只看游標前
    /// 那一段空白的話，<c>FROM dbo.Loan⏎/* 接續 */ </c> 會被當成同一行的別名位置。
    ///
    /// 中間什麼都沒有時不算：那代表兩個詞元是連著的（<c>'x'|</c>），不是別名的位置。
    /// </remarks>
    private bool StaysOnSameLine()
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        var previousEnd = tokens[tokens.Count - 1].End;

        return previousEnd < textBeforeToken.Length &&
            !StartsOnNewLine(previousEnd, textBeforeToken.Length, textBeforeToken);
    }

    /// <summary>
    /// 選取清單或資料來源清單的最後一項剛寫完，而且還沒有別名。
    /// </summary>
    /// <remarks>
    /// 一條規則涵蓋兩種清單：<b>最後一個運算元前面不緊鄰另一個運算元</b>。
    /// <c>SELECT a b </c>、<c>FROM t a </c> 的 <c>b</c>、<c>a</c> 前面緊鄰一個運算元，
    /// 那就是別名，已經寫完了；<c>AS</c> 同理。
    ///
    /// 運算元的結尾是識別字（含多段名稱）、變數、右括號、字串與數值常值、
    /// <c>CASE … END</c> 的 <c>END</c>，以及 <c>NULL</c> 這種自成一項的關鍵字；
    /// <c>*</c> 不算——<c>SELECT * </c> 與 <c>SELECT t.* </c> 後面不能接別名。
    ///
    /// 兩種清單的差別只在「一項」長什麼樣：資料來源是一個名稱或一次資料表值函式
    /// 呼叫，前面必須直接是 FROM／JOIN／APPLY／USING 或 FROM 清單的逗號；選取清單
    /// 的一項是一整個運算式（<c>a + b</c>、<c>COUNT(*)</c>），往回走到 SELECT、
    /// 逗號或 TOP 子句才算數，途中穿過沒關上的括號或 CASE 就不是清單這一層。
    /// </remarks>
    private bool IsUnaliasedItem(
        int last,
        SqlKeywordPosition keywords)
    {
        // 函式呼叫之後的 OVER 是加在子句尾端上的，不改變這一項接不接別名。
        keywords &= ~SqlKeywordPosition.FunctionCallTail;
        var dataSource = keywords == SqlKeywordPosition.TableSourceTail;

        if (!dataSource && keywords != SqlKeywordPosition.SelectListTail)
        {
            return false;
        }

        var start = FindOperandStart(last, dataSource);

        if (start < 1)
        {
            return false;
        }

        return dataSource
            ? OpensDataSource(start - 1)
            : OpensSelectItem(start - 1);
    }

    /// <summary>
    /// <paramref name="last"/> 是一個運算元的結尾時，回傳那個運算元的第一個詞元；否則 -1。
    /// </summary>
    /// <param name="dataSource">
    /// 資料來源那一層：只收名稱、變數與資料表值函式呼叫。常值不是資料來源，
    /// 而前面不是函式名稱的括號是資料表提示（<c>WITH (NOLOCK)</c>）或括號包起來的
    /// 聯結，兩者後面都不接別名。
    ///
    /// 資料來源的後綴屬於同一個運算元，別名寫在整個後綴之後：
    /// <c>FOR SYSTEM_TIME …</c>（見 <see cref="FindTemporalClause"/>），以及資料表值函式
    /// 呼叫後面緊接的 <c>WITH (…)</c> 資料行結構描述（<c>OPENJSON(@j) WITH (a int)</c>）。
    /// 名稱後面的 <c>WITH (…)</c> 是資料表提示，不是這一種。
    /// </param>
    private int FindOperandStart(int last, bool dataSource)
    {
        if (dataSource && FindTemporalClause(last) is var temporal and >= 1)
        {
            last = temporal - 1;
        }

        var token = tokens[last];

        if (token.IsPunctuation(")"))
        {
            var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, last);

            while (dataSource &&
                   open >= 2 &&
                   tokens[open - 1].IsKeyword("WITH") &&
                   tokens[open - 2].IsPunctuation(")"))
            {
                open = SqlTokenNavigator.FindOpeningParenthesis(tokens, open - 2);
            }

            if (open < 0)
            {
                return -1;
            }

            // 資料來源那一層連關鍵字也當成名稱收進來：OPENJSON、OPENROWSET 是關鍵字，
            // 而 WITH、FROM 收進來之後前面接的不是資料來源的開頭，下一步自己會擋掉。
            var name = open - 1;

            if (name >= 0 &&
                tokens[name].Kind == SqlTokenKind.Identifier &&
                (dataSource || !IsBareKeyword(name)))
            {
                return SqlTokenNavigator.SkipQualifiedNameBackward(tokens, name);
            }

            return dataSource ? -1 : open;
        }

        switch (token.Kind)
        {
            case SqlTokenKind.Variable:
                return last;
            case SqlTokenKind.Number:
            case SqlTokenKind.String:
                return dataSource ? -1 : last;
            case SqlTokenKind.Identifier when !IsBareKeyword(last):
                return SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last);
            case SqlTokenKind.Identifier when !dataSource && token.IsKeyword("END"):
                return FindCaseStart(last);
            case SqlTokenKind.Identifier when !dataSource && SqlKeywordCatalog.EndsItem(token.Value):
                return last;
            default:
                return -1;
        }
    }

    /// <summary>
    /// 沒加引號、而且是關鍵字的識別字；點號後面那一段是名稱，不算。
    /// </summary>
    private bool IsBareKeyword(int index)
    {
        var token = tokens[index];

        return !token.IsQuoted &&
            !(index >= 1 && tokens[index - 1].IsPunctuation(".")) &&
            SqlKeywordCatalog.IsKeyword(token.Value);
    }

    /// <summary><paramref name="previous"/> 之後開始的是資料來源清單裡接得了別名的一項。</summary>
    /// <remarks>
    /// MERGE 的目標（<c>MERGE INTO t </c>、<c>MERGE t </c>）也算：它與 USING 的來源
    /// 一樣接得了別名，而 MERGE 與 USING 都是 <see cref="ClauseAnchors"/> 的錨點。
    ///
    /// 不接資料來源的 FROM（<see cref="IntroducesDataSource"/>）與 DELETE 自己的 FROM 不算，
    /// 見 <see cref="NamesDeleteTarget"/>。
    /// </remarks>
    private bool OpensDataSource(int previous)
    {
        var token = tokens[previous];

        if (token.Kind == SqlTokenKind.Identifier &&
            !token.IsQuoted &&
            (TableSourceKeywords.Contains(token.Value) ||
             token.IsKeyword("MERGE") ||
             (token.IsKeyword("INTO") && previous >= 1 && tokens[previous - 1].IsKeyword("MERGE"))))
        {
            return !token.IsKeyword("FROM") || (IntroducesDataSource(previous) && !NamesDeleteTarget(previous));
        }

        // FROM a, b | 的逗號也開啟一個資料來源，但 SELECT a, b | 的不是。
        return token.IsPunctuation(",")
            && FindAnchorPosition(previous - 1, ListAnchors) == SqlKeywordPosition.DataSource;
    }

    /// <summary><paramref name="from"/> 的 FROM 帶出的是 DELETE 自己的目標。</summary>
    /// <remarks>
    /// <c>DELETE [TOP (5)] FROM t</c> 的 FROM 後面是動詞的目標，文法不接別名；
    /// <c>DELETE a FROM t a JOIN …</c> 的第二個 FROM 才是一般的資料來源。
    /// DELETE 的 FROM 前面還沒寫目標名稱時就是目標。
    /// </remarks>
    private bool NamesDeleteTarget(int from)
    {
        var verb = FindVerb(from - 1);

        if (verb < 0 || !tokens[verb].IsKeyword("DELETE"))
        {
            return false;
        }

        for (var index = from - 1; index > verb; index--)
        {
            if (tokens[index].IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);
                continue;
            }

            if (tokens[index].Kind == SqlTokenKind.Identifier && !IsBareKeyword(index))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 最後一個運算元前面是 <paramref name="previous"/>，而它屬於選取清單這一層的
    /// 同一項，而且那一項還沒有別名。
    /// </summary>
    private bool OpensSelectItem(int previous)
    {
        // 緊鄰另一個運算元或 AS：別名已經寫了。TOP 的引數例外，它不是清單的一項。
        if (tokens[previous].IsKeyword("AS") ||
            (FindOperandStart(previous, dataSource: false) >= 0 &&
             !EndsTopClause(previous)))
        {
            return false;
        }

        for (var index = previous; index >= 0; index--)
        {
            var token = tokens[index];

            if (EndsTopClause(index) ||
                token.IsKeyword("SELECT") ||
                token.IsPunctuation(","))
            {
                return true;
            }

            if (token.IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            if (token.IsKeyword("END"))
            {
                index = FindCaseStart(index);

                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            // 沒關上的括號是函式引數或子查詢的裡面；CASE 的各段是 CASE 的裡面。
            // 兩者都不是清單這一層，後面接的不會是別名。
            if (token.IsPunctuation("(") ||
                token.IsKeyword("CASE") ||
                token.IsKeyword("WHEN") ||
                token.IsKeyword("THEN") ||
                token.IsKeyword("ELSE"))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="last"/> 是 <c>SELECT [DISTINCT|ALL] TOP …</c> 子句的最後一個詞元。
    /// </summary>
    /// <remarks>
    /// <c>TOP 10</c>、<c>TOP (10)</c>、<c>TOP @n</c>，後面可以再接 <c>PERCENT</c> 與
    /// <c>WITH TIES</c>。TOP 的引數不是選取清單的第一項：當成一項的話，
    /// <c>SELECT TOP 10 </c> 會判成清單尾端，列出 FROM 而不是欄位與 CASE。
    ///
    /// 只認 SELECT 的 TOP：<c>INSERT TOP (5)</c>、<c>DELETE TOP (5)</c> 之後接的是
    /// INTO、FROM，那兩處照舊由它們自己的子句決定。
    /// </remarks>
    private bool EndsTopClause(int last)
    {
        var index = last;

        if (tokens[index].IsKeyword("TIES"))
        {
            if (index < 1 || !tokens[index - 1].IsKeyword("WITH"))
            {
                return false;
            }

            index -= 2;
        }

        if (index >= 0 && tokens[index].IsKeyword("PERCENT"))
        {
            index--;
        }

        if (index < 0)
        {
            return false;
        }

        int top;

        if (tokens[index].IsPunctuation(")"))
        {
            top = SqlTokenNavigator.FindOpeningParenthesis(tokens, index) - 1;
        }
        else if (tokens[index].Kind is SqlTokenKind.Number or SqlTokenKind.Variable)
        {
            top = index - 1;
        }
        else
        {
            return false;
        }

        if (top < 1 || !tokens[top].IsKeyword("TOP"))
        {
            return false;
        }

        var before = top - 1;

        if (tokens[before].IsKeyword("DISTINCT") || tokens[before].IsKeyword("ALL"))
        {
            before--;
        }

        return before >= 0 && tokens[before].IsKeyword("SELECT");
    }

    /// <summary>
    /// <paramref name="last"/> 結束一個 <c>FOR SYSTEM_TIME</c> 子句時回傳那個 FOR；否則 -1。
    /// </summary>
    /// <remarks>
    /// 五種寫法：<c>ALL</c>、<c>AS OF v</c>、<c>FROM v TO v</c>、<c>BETWEEN v AND v</c>、
    /// <c>CONTAINED IN (v, v)</c>，v 是常值或變數。只認寫完的：<c>AS OF </c> 還在等值，
    /// 那時的位置與名字都照一般規則判——<c>FOR SYSTEM_TIME AS </c> 不能被當成別名。
    /// </remarks>
    private int FindTemporalClause(int last)
    {
        var index = last;

        if (tokens[index].IsPunctuation(")"))
        {
            index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

            if (index < 2 || !tokens[index - 1].IsKeyword("IN") || !tokens[index - 2].IsKeyword("CONTAINED"))
            {
                return -1;
            }

            index -= 3;
        }
        else if (tokens[index].IsKeyword("ALL"))
        {
            index--;
        }
        else if (IsTemporalValue(tokens[index]) && index >= 2)
        {
            var keyword = tokens[index - 1];

            if (keyword.IsKeyword("OF") && tokens[index - 2].IsKeyword("AS"))
            {
                index -= 3;
            }
            else if (index >= 4 &&
                     IsTemporalValue(tokens[index - 2]) &&
                     ((keyword.IsKeyword("TO") && tokens[index - 3].IsKeyword("FROM")) ||
                      (keyword.IsKeyword("AND") && tokens[index - 3].IsKeyword("BETWEEN"))))
            {
                index -= 4;
            }
            else
            {
                return -1;
            }
        }
        else
        {
            return -1;
        }

        return index >= 1 && tokens[index].IsKeyword("SYSTEM_TIME") && tokens[index - 1].IsKeyword("FOR")
            ? index - 1
            : -1;
    }

    private static bool IsTemporalValue(SqlToken token) =>
        token.Kind is SqlTokenKind.String or SqlTokenKind.Variable or SqlTokenKind.Number;

    /// <summary>
    /// <paramref name="end"/> 的 <c>END</c> 收的是一個 <c>CASE</c> 時，回傳那個 CASE；否則 -1。
    /// </summary>
    /// <remarks>
    /// END 也收 <c>BEGIN … END</c> 與 <c>BEGIN TRY … END TRY</c>。往回數的路上走到這一句的
    /// 開頭就代表這不是 CASE 的：CASE 是運算式，跨不過語句的界線。
    /// 括號整組跳過，理由與 <see cref="FindAnchorPosition"/> 相同。
    /// </remarks>
    private int FindCaseStart(int end)
    {
        if (end + 1 < tokens.Count &&
            (tokens[end + 1].IsKeyword("TRY") || tokens[end + 1].IsKeyword("CATCH")))
        {
            return -1;
        }

        return FindUnclosedCase(end - 1);
    }

    /// <summary>
    /// <paramref name="from"/> 以前還沒以 END 收掉的 CASE；沒有就是 -1。
    /// </summary>
    /// <remarks>
    /// 停下來的條件與 <see cref="FindCaseStart"/> 相同：語句的界線不會出現在 CASE 裡面。
    /// </remarks>
    private int FindUnclosedCase(int from)
    {
        var depth = 0;

        for (var index = from; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return -1;
                }

                continue;
            }

            if (token.IsPunctuation(";") || token.IsKeyword("GO") || IsStatementHead(index))
            {
                return -1;
            }

            if (SqlTokenNavigator.ClosesBlock(tokens, index))
            {
                depth++;
            }
            else if (token.IsKeyword("CASE") && depth-- == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// <paramref name="last"/> 是語句區塊的邊界時，之後的位置；不是就回 null。
    /// </summary>
    /// <remarks>
    /// 區塊的開頭與結尾之後都是下一句：<c>BEGIN TRY</c>、<c>BEGIN CATCH</c>、<c>END TRY</c>、
    /// <c>END CATCH</c>，以及 IF 的 <c>ELSE</c>。<c>BEGIN … END</c> 的 END 之後還接得了
    /// ELSE、TRY、CATCH，那是 <see cref="SqlKeywordPosition.BlockEnd"/>。
    ///
    /// CASE 的 ELSE 與 END 不是：還沒以 END 收掉的 CASE 裡的 ELSE 接的是運算式，
    /// 寫完的 CASE … END 由呼叫端先當成運算元處理。
    /// </remarks>
    private SqlKeywordPosition? FindBlockBoundary(int last)
    {
        var token = tokens[last];

        if (token.IsKeyword("TRY") || token.IsKeyword("CATCH"))
        {
            return last >= 1 && (tokens[last - 1].IsKeyword("BEGIN") || tokens[last - 1].IsKeyword("END"))
                ? SqlKeywordPosition.StatementStart
                : null;
        }

        if (token.IsKeyword("ELSE"))
        {
            return FindUnclosedCase(last - 1) < 0 ? SqlKeywordPosition.StatementStart : null;
        }

        return SqlTokenNavigator.ClosesBlock(tokens, last)
            ? SqlKeywordPosition.BlockEnd | SqlKeywordPosition.StatementStart
            : null;
    }

    /// <summary>
    /// <paramref name="last"/> 這一格是使用者正要取的新名字，或可能是。
    /// </summary>
    /// <remarks>
    /// 四種都是「前面的字已經說完這裡要一個還不存在的名稱」：
    ///
    /// <list type="bullet">
    /// <item><c>CREATE PROCEDURE </c>、<c>CREATE UNIQUE INDEX </c>、<c>CREATE SYMMETRIC KEY </c> 這類建立敘述的物件名稱（<see cref="CreatedKinds"/>），
    /// 哪一段是新名字見 <see cref="CreatedNameSlot"/>。<c>ALTER PROCEDURE </c> 要的是既有物件，不在這裡。</item>
    /// <item><c>SELECT … INTO </c> 的目標。<c>INSERT INTO </c>、<c>MERGE INTO </c> 要的是
    /// 既有資料表，<c>FETCH … INTO </c> 與 <c>OUTPUT … INTO </c> 的子句錨點不是 SELECT，
    /// 都不在這裡。</item>
    /// <item>一句開頭的 <c>WITH </c> 與 <c>WITH c AS (…), </c> 的 CTE 名稱。資料表提示與選項的
    /// <c>WITH</c> 不是一句的開頭，見 <see cref="IsStatementHead"/>。</item>
    /// </list>
    ///
    /// 帶限定字時一樣：<c>CREATE PROCEDURE dbo.</c> 的點號由 <see cref="AnalyzeAt"/>
    /// 剝掉之後回到這裡，<paramref name="qualified"/> 記著剝過。
    /// </remarks>
    private bool TryResolveNewName(int last, bool qualified, out SqlCaretPosition caret)
    {
        var token = tokens[last];

        if (token.IsPunctuation(",") && last >= 1 && EndsCommonTableExpression(last - 1))
        {
            caret = new SqlCaretPosition(SqlKeywordPosition.Any, SqlCompletionSlot.Name);
            return true;
        }

        if (token.IsKeyword("WITH") && IsStatementHead(last))
        {
            caret = new SqlCaretPosition(SqlKeywordPosition.Any, SqlCompletionSlot.Name);
            return true;
        }

        // 新資料表寫完之後接的是 FROM、WHERE。
        if (token.IsKeyword("INTO") && IsSelectInto(last))
        {
            caret = new SqlCaretPosition(SqlKeywordPosition.SelectIntoTail, SqlCompletionSlot.Name);
            return true;
        }

        if (token.Kind == SqlTokenKind.Identifier &&
            !token.IsQuoted &&
            FindCreatedKind(last, endsAt: true) is { } kind)
        {
            caret = CreatedNameSlot(kind, qualified);
            return true;
        }

        caret = default;
        return false;
    }

    /// <summary>
    /// 建立敘述的物件種類：<paramref name="endsAt"/> 為真時是以 <paramref name="index"/> 結尾、前面緊接 CREATE 的那一種，
    /// 否則是 <paramref name="index"/> 的 CREATE 之後寫的那一種；都不是回 null。
    /// </summary>
    private (string[] Words, bool MayExist, bool SchemaQualified)? FindCreatedKind(int index, bool endsAt)
    {
        foreach (var kind in CreatedKinds)
        {
            var create = endsAt ? index - kind.Words.Length : index;

            if (create >= 0 &&
                create + kind.Words.Length < tokens.Count &&
                tokens[create].IsKeyword("CREATE") &&
                WritesWords(create + 1, kind.Words))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>從 <paramref name="start"/> 起的詞元正好寫著 <paramref name="words"/>。</summary>
    private bool WritesWords(int start, string[] words)
    {
        for (var offset = 0; offset < words.Length; offset++)
        {
            if (!tokens[start + offset].IsKeyword(words[offset]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>建立敘述的名稱那一格，名稱寫到 <paramref name="qualified"/> 那一段時是什麼名字。</summary>
    /// <remarks>
    /// 一條規則：<b>這一段列得出東西就是「可能是名字」，列不出才是新名字</b>。列得出的有三種：
    /// 那一格可能是既有物件（<c>CREATE OR ALTER</c>）、也接得上別的字（<c>CREATE DATABASE SCOPED</c>），
    /// 或是還沒寫限定字而名稱寫得出結構描述（<c>CREATE PROCEDURE dbo.p</c> 的 <c>dbo</c>）——新的只有最後一段。
    /// 前兩種每一段都照目標列既有的名稱，結構描述已在其中；只剩第三種的那一段只列結構描述
    /// （<see cref="SqlCaretPosition.NewNameQualifier"/>），點號之後回到新名字。
    /// </remarks>
    private static SqlCaretPosition CreatedNameSlot((string[] Words, bool MayExist, bool SchemaQualified) kind, bool qualified)
    {
        if (kind.MayExist)
        {
            return new SqlCaretPosition(SqlKeywordPosition.Any, SqlCompletionSlot.MaybeName);
        }

        return kind.SchemaQualified && !qualified
            ? new SqlCaretPosition(SqlKeywordPosition.Any, SqlCompletionSlot.MaybeName, newNameQualifier: true)
            : new SqlCaretPosition(SqlKeywordPosition.Any, SqlCompletionSlot.Name);
    }

    private static (string[] Words, bool MayExist, bool SchemaQualified)[] BuildCreatedKinds()
    {
        var kinds = new List<(string[] Words, bool MayExist, bool SchemaQualified)>();

        foreach (var (kind, alsoWords, schemaQualified) in SqlKeywordCatalogData.CreatedKinds)
        {
            var mayExist = alsoWords || kind.StartsWith("OR ALTER ", StringComparison.OrdinalIgnoreCase);
            kinds.Add((kind.Split(' '), mayExist, schemaQualified));
        }

        kinds.Sort((left, right) => right.Words.Length.CompareTo(left.Words.Length));
        return kinds.ToArray();
    }

    /// <summary><paramref name="into"/> 是 <c>SELECT … INTO</c> 的 INTO。</summary>
    private bool IsSelectInto(int into)
    {
        return into >= 1 &&
            !tokens[into - 1].IsKeyword("INSERT") &&
            !tokens[into - 1].IsKeyword("MERGE") &&
            FindClausePosition(into - 1) == SqlKeywordPosition.SelectListTail;
    }

    /// <summary>
    /// <paramref name="close"/> 的右括號收掉一個 CTE：<c>WITH a AS (…)</c>、<c>WITH a AS (…), b AS (…)</c>。
    /// </summary>
    /// <remarks>
    /// 往回認的形狀是 <c>名稱 [(資料行…)] AS (…)</c>，一路認到開頭的 WITH；
    /// 中間每一個逗號都要是同一種形狀。用迴圈而不是遞迴，理由與
    /// <see cref="SqlTokenNavigator.OpensQuery"/> 相同。
    /// </remarks>
    private bool EndsCommonTableExpression(int close)
    {
        var index = close;

        while (index >= 0 && tokens[index].IsPunctuation(")"))
        {
            var body = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

            if (body < 2 || !tokens[body - 1].IsKeyword("AS"))
            {
                return false;
            }

            var name = body - 2;

            if (tokens[name].IsPunctuation(")"))
            {
                name = SqlTokenNavigator.FindOpeningParenthesis(tokens, name) - 1;
            }

            if (name < 1 || tokens[name].Kind != SqlTokenKind.Identifier)
            {
                return false;
            }

            if (tokens[name - 1].IsKeyword("WITH"))
            {
                return IsStatementHead(name - 1);
            }

            if (!tokens[name - 1].IsPunctuation(","))
            {
                return false;
            }

            index = name - 2;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="open"/> 的左括號開啟的是資料行定義清單。
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE t (</c>、<c>DECLARE @t TABLE (</c>、<c>RETURNS @t TABLE (</c>、
    /// <c>CREATE TYPE x AS TABLE (</c>。這一層的每一項開頭是新資料行名稱，或 CONSTRAINT、
    /// PRIMARY KEY、INDEX 這些字，見 <see cref="SqlKeywordPosition.ColumnDefinition"/>。
    /// </remarks>
    private bool OpensColumnDefinitions(int open)
    {
        if (open < 1)
        {
            return false;
        }

        if (tokens[open - 1].IsKeyword("TABLE"))
        {
            return open >= 2 &&
                (tokens[open - 2].Kind == SqlTokenKind.Variable || tokens[open - 2].IsKeyword("AS"));
        }

        if (tokens[open - 1].Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        var start = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, open - 1);

        return start >= 2 &&
            tokens[start - 1].IsKeyword("TABLE") &&
            tokens[start - 2].IsKeyword("CREATE");
    }

    /// <summary>
    /// 游標落在 <c>ALTER TABLE</c> 的三個位置之一。
    /// </summary>
    /// <remarks>
    /// 這三處以前一律回 <see cref="SqlKeywordPosition.Any"/>，代價是整份關鍵字目錄與
    /// 所有片段全部進場——使用者在 <c>ADD </c> 之後看到的是整個資料庫，而文法上
    /// 對的只有九個字。
    ///
    /// 認的是「往回正好是 <c>ALTER TABLE</c> 加一個名稱單位」，緊鄰的形狀走不出這一句，
    /// 不必再問語句的界線。
    ///
    /// <c>ADD COLUMN</c> 不必判——T-SQL 沒有這種寫法，<c>COLUMN</c> 只跟在
    /// <c>ALTER</c> 與 <c>DROP</c> 後面。
    /// </remarks>
    private bool TryResolveAlterTable(
        int last,
        out SqlKeywordPosition position)
    {
        if (tokens[last].IsKeyword("ADD"))
        {
            position = SqlKeywordPosition.AlterTableAdd;
            return IsAlterTableTarget(last - 1);
        }

        if (tokens[last].IsKeyword("COLUMN"))
        {
            position = SqlKeywordPosition.AlterTableColumn;
            return last >= 1
                && (tokens[last - 1].IsKeyword("ALTER") || tokens[last - 1].IsKeyword("DROP"))
                && IsAlterTableTarget(last - 2);
        }

        position = SqlKeywordPosition.AlterTableAction;
        return IsAlterTableTarget(last);
    }

    /// <summary>
    /// <paramref name="comma"/> 是 <c>ALTER TABLE t ADD</c> 清單裡的逗號：同一層往回、走出這一句之前先碰到那個 ADD。
    /// </summary>
    /// <remarks>
    /// 途中的括號整組跳過（<c>CHECK (a > 0)</c>、<c>DEFAULT (0)</c>）；沒關上的左括號表示逗號在別的清單裡。
    /// </remarks>
    private bool ContinuesAlterTableAdd(int comma)
    {
        for (var index = comma - 1; index >= 2; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            if (token.IsPunctuation("(") || token.IsPunctuation(";"))
            {
                return false;
            }

            if (token.IsKeyword("ADD"))
            {
                return IsAlterTableTarget(index - 1);
            }

            if (IsStatementHead(index))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary><paramref name="last"/> 是 <c>ALTER TABLE</c> 目標名稱的最後一個詞元。</summary>
    private bool IsAlterTableTarget(int last)
    {
        if (last < 2 || tokens[last].Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        var start = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last);

        return start >= 2
            && tokens[start - 1].IsKeyword("TABLE")
            && tokens[start - 2].IsKeyword("ALTER");
    }

    /// <summary>
    /// 分析 <paramref name="last"/> 這個詞元之後的位置。
    /// </summary>
    /// <param name="followAlias">
    /// 允許為了判斷 <c>AS</c> 是不是別名而再往前看一格。這一層只有一層：
    /// <c>AS AS</c> 這種寫不出來的東西不該讓分析器把堆疊用完。往前一句再問的遞迴
    /// 另由 <see cref="KeywordsBefore"/> 擋住層數。
    /// </param>
    /// <param name="qualified">游標那個名稱已經寫了限定字，<paramref name="last"/> 在整個名稱之前。</param>
    private SqlCaretPosition AnalyzeAt(
        int last,
        bool followAlias,
        bool qualified = false)
    {
        if (last < 0)
        {
            return new SqlCaretPosition(SqlKeywordPosition.StatementStart);
        }

        // TOP 的引數要排在括號之前問：TOP (10) 的右括號不是一個算完的運算元。
        // 之後仍是選取清單的起點，另外接得了 PERCENT、WITH TIES；WITH TIES 寫完就不再接。
        if (EndsTopClause(last))
        {
            return new SqlCaretPosition(tokens[last].IsKeyword("TIES")
                ? SqlKeywordPosition.SelectList
                : SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail);
        }

        // FOR SYSTEM_TIME … 是資料表名稱的後綴，寫完之後的位置與名稱之後相同。
        if (FindTemporalClause(last) is var temporal and >= 1)
        {
            return AnalyzeAt(temporal - 1, followAlias);
        }

        var token = tokens[last];

        if (token.IsPunctuation(")"))
        {
            return AfterGroup(last);
        }

        // 尾端的點號是使用者正在打的那個名稱的一部分（dbo.、a.），不是一個算完的
        // 運算元：位置由整個名稱之前的東西決定，與名稱只打了一半沒有關係。
        // 少了這一條，限定字之後一律是 Any——症狀是 FROM a, dbo. 在位置上與
        // SELECT dbo. 沒有差別，而前者要的只有資料來源。
        //
        // 前面不是識別字時照舊：LibArchive.. 的空段不是限定字，而 1.5 的小數點
        // 根本走不到這裡——詞法分析把它掃成一個數值詞元。
        if (token.IsPunctuation(".") &&
            last >= 1 &&
            tokens[last - 1].Kind == SqlTokenKind.Identifier)
        {
            return AnalyzeAt(
                SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last - 1) - 1,
                followAlias,
                qualified: true);
        }

        // 加引號的識別字是名稱不是關鍵字：[AS] 之後不是別名位置。
        if (followAlias &&
            token.Kind == SqlTokenKind.Identifier &&
            !token.IsQuoted &&
            token.IsKeyword("AS"))
        {
            if (IntroducesAlias(last, out var afterAlias))
            {
                return new SqlCaretPosition(afterAlias, SqlCompletionSlot.Name);
            }

            return new SqlCaretPosition(OpensModuleBody(last)
                ? SqlKeywordPosition.StatementStart
                : SqlKeywordPosition.Any);
        }

        if (TryResolveNewName(last, qualified, out var named))
        {
            return named;
        }

        // 資料行定義的起點可能是新資料行的名稱，也可能是 CONSTRAINT、PRIMARY KEY——
        // ALTER TABLE t ADD 與 CREATE TABLE t ( 都是這種格子，同一條規則；
        // 兩者接得了的關鍵字不一樣，所以是兩個位置。結果集的資料行定義只寫得出名稱。
        var keywords = KeywordsAfter(last);

        return new SqlCaretPosition(
            keywords,
            keywords switch
            {
                SqlKeywordPosition.AlterTableAdd or SqlKeywordPosition.ColumnDefinition => SqlCompletionSlot.MaybeName,
                SqlKeywordPosition.ResultSetColumn => SqlCompletionSlot.Name,
                _ => SqlCompletionSlot.Grammar
            });
    }

    /// <summary>
    /// <paramref name="last"/> 這個詞元之後接得了哪些關鍵字；
    /// 括號、限定字與 <c>AS</c> 由 <see cref="AnalyzeAt"/> 先處理。
    /// </summary>
    private SqlKeywordPosition KeywordsAfter(int last)
    {
        var token = tokens[last];

        if (token.IsPunctuation(";"))
        {
            return SqlKeywordPosition.StatementStart;
        }

        // 要排在逗號、WHEN 與「認得但沒有對應位置」之前：那幾條會把這些格子當成清單、述詞或 Any。
        if (FindStatementSlot(last) is { } slot)
        {
            return slot;
        }

        // 資料行定義清單的開頭與逗號之後。逗號要先問這一條：往回找清單錨點時會穿過
        // 那個左括號，走到 CREATE 之前的別的子句。
        if ((token.IsPunctuation("(") && OpensColumnDefinitions(last)) ||
            (token.IsPunctuation(",") &&
             OpensColumnDefinitions(FindUnclosedParenthesis(last - 1))))
        {
            return SqlKeywordPosition.ColumnDefinition;
        }

        // ALTER TABLE t ADD a int, | 是新增清單的下一項，與 ADD 之後同一格。
        if (token.IsPunctuation(",") && ContinuesAlterTableAdd(last))
        {
            return SqlKeywordPosition.AlterTableAdd;
        }

        // SELECT a, | 與 FROM a, | 都是清單再來一項，位置回到清單的起點。
        // ORDER BY a, | 也一樣：下一項仍然是欄位。
        if (token.IsPunctuation(","))
        {
            return FindAnchorPosition(last - 1, ListAnchors);
        }

        if (token.Kind is SqlTokenKind.Punctuation or SqlTokenKind.Operator)
        {
            return SqlKeywordPosition.Any;
        }

        // SET 選項要排在 AfterKeyword 之前：SET ANSI_NULLS ON 的 ON 是選項值，
        // 交給那一條的話會被當成 JOIN 的 ON，下一行打 GO 只剩 GROUPING 這種字。
        switch (FindSetOptionPart(last))
        {
            case SetOptionPart.Name:
                return SqlKeywordPosition.SetOptionValue;
            case SetOptionPart.Value:
                // 選項值寫完，這一句就結束了：SET 選項沒有可以續寫的子句。
                return SqlKeywordPosition.StatementStart;
        }

        // 加引號的識別字是名稱不是關鍵字：[FROM] 之後不是資料來源位置。
        if (token.Kind == SqlTokenKind.Identifier && !token.IsQuoted)
        {
            // 游標、備份裝置與主體前面的 FROM、變數清單前面的 INTO 不接資料來源，這裡判不出位置；
            // 游標名稱前的 GLOBAL 也是。
            if (((token.IsKeyword("FROM") || token.IsKeyword("INTO")) && !IntroducesDataSource(last)) ||
                IntroducesCursor(last))
            {
                return SqlKeywordPosition.Any;
            }

            if (AfterKeyword.TryGetValue(token.Value, out var position))
            {
                return position;
            }

            if (IsOrderOrGroupBy(last))
            {
                return SqlKeywordPosition.OrderByColumn;
            }

            // ALTER TABLE 的三個位置要排在「認得但沒有對應位置」之前：ADD 與 COLUMN
            // 都是目錄認得的關鍵字，讓那一條先接走的話這裡永遠回 Any。
            if (TryResolveAlterTable(last, out var alterPosition))
            {
                return alterPosition;
            }

            // CASE … END 是一個算完的運算元，與一整組括號同一個道理。
            if (token.IsKeyword("END") && FindCaseStart(last) is var caseStart and >= 0)
            {
                return FindClausePosition(caseStart - 1);
            }

            if (FindBlockBoundary(last) is { } boundary)
            {
                return boundary;
            }

            if (ClosesStatement(last))
            {
                return SqlKeywordPosition.StatementStart;
            }

            // NULL、CURRENT_USER、DESC 本身就把那一項寫完，之後與識別字之後相同。
            if (SqlKeywordCatalog.EndsItem(token.Value))
            {
                return FindClausePosition(last - 1);
            }

            if (SqlKeywordCatalog.IsKeyword(token.Value))
            {
                // 認得但沒有對應位置的關鍵字（THEN、CASE 的 ELSE…），不猜。
                return SqlKeywordPosition.Any;
            }
        }

        // 變數也走這裡：@x 之後是一個算完的運算元。
        return FindClausePosition(last);
    }

    /// <summary>SET 選項裡，游標前一個詞元屬於哪一段。</summary>
    private enum SetOptionPart
    {
        /// <summary>不在 SET 選項裡，或選項名稱還沒寫完。</summary>
        None,

        /// <summary>選項名稱剛寫完：<c>SET NOCOUNT </c>。</summary>
        Name,

        /// <summary>選項值已經寫了：<c>SET NOCOUNT ON </c>。</summary>
        Value
    }

    /// <summary>
    /// <paramref name="last"/> 落在 SET 選項的名稱還是值裡：<c>SET NOCOUNT </c>、
    /// <c>SET IDENTITY_INSERT dbo.t </c>、<c>SET TRANSACTION ISOLATION LEVEL </c> 是名稱剛寫完，
    /// <c>SET NOCOUNT ON </c>、<c>SET ROWCOUNT 10 </c>、<c>… LEVEL READ COMMITTED </c> 是值已經寫了。
    /// </summary>
    /// <remarks>
    /// 名稱從 SET 後面第一個字開始（可以是關鍵字），接著穿過不是關鍵字的名稱、點號與
    /// 選項清單的逗號（<c>SET ANSI_NULLS, QUOTED_IDENTIFIER </c>）；其後的關鍵字、數值、
    /// 字串或變數起是值。這條切法照的是剖析器：接 ON／OFF 的選項（NOCOUNT、ANSI_NULLS、
    /// XACT_ABORT）在 ScriptDom 裡是一般識別字，有自己文法的（ROWCOUNT、IDENTITY_INSERT、
    /// TRANSACTION、STATISTICS）才是關鍵字——所以名稱停在關鍵字上時還沒寫完
    /// （<c>SET IDENTITY_INSERT </c> 之後要的是資料表），不是選項值的位置。
    ///
    /// 識別字的值（<c>SET DATEFORMAT dmy</c>、<c>ALTER DATABASE x SET RECOVERY SIMPLE</c>）與
    /// 名稱的延續（<c>SET IDENTITY_INSERT t</c>）在詞元上分不開，一律算名稱；那種值寫完再換行時
    /// 由 <see cref="AddStatementStartOnNewLine"/> 補上語句開頭。
    ///
    /// SET 要是 <paramref name="last"/> 所屬的動詞（<see cref="FindVerb"/>），中間只有名稱與值寫得出的
    /// 詞元：<c>SET NOCOUNT ON SELECT a FROM t </c> 的 <c>t</c> 屬於 SELECT。
    /// SET 本身還要是選項的 SET，見 <see cref="IntroducesOptions"/>。
    /// </remarks>
    private SetOptionPart FindSetOptionPart(int last)
    {
        if (!CanBelongToSetOption(tokens[last]))
        {
            return SetOptionPart.None;
        }

        var set = FindVerb(last);

        if (set < 0 ||
            set == last ||
            !tokens[set].IsKeyword("SET") ||
            tokens[set + 1].Kind != SqlTokenKind.Identifier)
        {
            return SetOptionPart.None;
        }

        for (var index = set + 1; index < last; index++)
        {
            if (!CanBelongToSetOption(tokens[index]))
            {
                return SetOptionPart.None;
            }
        }

        if (!IntroducesOptions(set))
        {
            return SetOptionPart.None;
        }

        var nameEnd = set + 1;

        while (nameEnd < last)
        {
            var next = tokens[nameEnd + 1];

            if (next.IsPunctuation(".") ||
                (next.Kind == SqlTokenKind.Identifier && !IsBareKeyword(nameEnd + 1)))
            {
                nameEnd++;
            }
            else if (next.IsPunctuation(",") &&
                     nameEnd + 2 <= last &&
                     tokens[nameEnd + 2].Kind == SqlTokenKind.Identifier)
            {
                nameEnd += 2;
            }
            else
            {
                break;
            }
        }

        if (last > nameEnd)
        {
            return SetOptionPart.Value;
        }

        return IsBareKeyword(nameEnd) ? SetOptionPart.None : SetOptionPart.Name;
    }

    /// <summary>SET 選項的名稱或值寫得出這個詞元：名稱、數值（含正負號）、字串、變數、點號與逗號。</summary>
    private static bool CanBelongToSetOption(SqlToken token)
    {
        return token.Kind switch
        {
            SqlTokenKind.Identifier or SqlTokenKind.Number or SqlTokenKind.String or SqlTokenKind.Variable => true,
            SqlTokenKind.Punctuation => token.Value is "." or ",",
            SqlTokenKind.Operator => token.Value is "-" or "+",
            _ => false
        };
    }

    /// <summary>
    /// <paramref name="setIndex"/> 的 <c>SET</c> 帶出的是選項，而不是 UPDATE 的資料行指派。
    /// </summary>
    /// <remarks>
    /// SET 前一格是明確的語句界線（開頭、BEGIN、模組主體的 AS、IF 的條件）時是選項：
    /// <c>ALTER PROCEDURE p AS SET</c> 與觸發程序的 <c>AFTER UPDATE AS SET</c> 都是一句的開始。
    /// 其餘看它接在哪一個動詞後面（<see cref="FindVerb"/>）：UPDATE 就是資料行指派
    /// （<c>UPDATE t⏎SET</c>、MERGE 的 <c>THEN UPDATE SET</c>）；別的動詞或沒有就是選項——
    /// <c>UPDATE t SET a = 1 SET NOCOUNT</c> 先碰到的是前一個 SET，後面那個 SET 因此是新的一句。
    /// 換行補上的語句開頭不算：<c>UPDATE t⏎SET</c> 最常見。
    ///
    /// <c>ALTER DATABASE x SET ANSI_NULLS ON</c> 的 SET 也是選項：名稱與值的形狀與 SET 敘述相同，
    /// 值寫完同樣結束這一句。
    /// </remarks>
    private bool IntroducesOptions(int setIndex)
    {
        var before = KeywordsBefore(setIndex);

        if (before != SqlKeywordPosition.Any && (before & StatementBoundaries) != SqlKeywordPosition.None)
        {
            return true;
        }

        var verb = FindVerb(setIndex - 1);

        return verb < 0 || !tokens[verb].IsKeyword("UPDATE");
    }

    /// <summary>
    /// <paramref name="asIndex"/> 的 <c>AS</c> 後面接的是別名，而不是別的東西。
    /// </summary>
    /// <param name="afterAlias">別名寫完之後的位置。</param>
    /// <remarks>
    /// <c>AS</c> 在 T-SQL 裡接兩種完全不同的東西，而分辨它們的線索不在後面
    /// （後面還沒打出來）而在前面：
    ///
    /// <list type="bullet">
    /// <item>一個運算式或一個資料來源剛寫完 → 後面是<b>別名</b>：
    /// <c>SELECT x AS </c>、<c>FROM t AS </c>、<c>FROM (SELECT …) AS </c>。</item>
    /// <item>其餘 → 後面不是名字，清單照常：<c>CREATE PROCEDURE p AS </c> 之後
    /// 是主體（見 <see cref="OpensModuleBody"/>），<c>CAST(x AS </c> 之後是型別，
    /// <c>EXECUTE AS </c> 之後是 USER。</item>
    /// </list>
    ///
    /// 所以問的是同一個問題：<c>AS</c> 前面是不是一項剛寫完、還沒有別名的清單項目——
    /// 與同一行沒有 AS 的別名是同一條規則（<see cref="IsUnaliasedItem"/>）。只看位置是
    /// 資料來源尾端不夠：<c>FROM t FOR SYSTEM_TIME AS </c> 也在那個位置，後面接的卻是
    /// <c>OF</c>。衍生資料表那一格本來就是名字——它的別名是文法強制的，多一個
    /// <c>AS</c> 不改變這件事。
    ///
    /// 比的是<b>整個值相等</b>而不是位元交集：判不出位置時回傳的
    /// <see cref="SqlKeywordPosition.Any"/> 含著上面那兩個旗標，用交集的話
    /// <c>CREATE PROCEDURE p AS </c> 也會被當成別名，主體開頭的 BEGIN、SELECT
    /// 就整組消失——這裡的 fail-open 必須真的 open。
    /// </remarks>
    private bool IntroducesAlias(
        int asIndex,
        out SqlKeywordPosition afterAlias)
    {
        var before = AnalyzeAt(asIndex - 1, followAlias: false);
        afterAlias = before.Keywords;

        return before.Slot == SqlCompletionSlot.Name
            || IsUnaliasedItem(asIndex - 1, before.Keywords);
    }

    /// <summary>
    /// <paramref name="asIndex"/> 的 <c>AS</c> 結束了程序、函式、觸發程序或檢視的標頭，
    /// 後面是主體：一句的開頭。
    /// </summary>
    /// <remarks>
    /// 少了這一條，主體的開頭判不出位置：清單是整份目錄，而 <c>SET NOCOUNT </c> 這種
    /// 子句片語只能以「可能」的身分加字，不能換掉整份清單（見 <see cref="SqlClausePhraseMatch"/>）。
    ///
    /// 這一句（<see cref="FindStatementStart"/>）要以 CREATE、ALTER 或 CREATE OR ALTER 接模組種類開頭。
    /// 標頭裡的 AS 只有 <c>EXECUTE AS</c> 與參數的 <c>@a AS int</c>；主體裡的 AS 屬於主體那一句，
    /// 走不回 CREATE。
    /// </remarks>
    private bool OpensModuleBody(int asIndex)
    {
        if (BelongsToModuleHeader(asIndex))
        {
            return false;
        }

        var start = FindStatementStart(asIndex - 1);

        if (start >= asIndex || !(tokens[start].IsKeyword("CREATE") || tokens[start].IsKeyword("ALTER")))
        {
            return false;
        }

        var kind = start + 1;

        if (tokens[start].IsKeyword("CREATE") &&
            kind + 1 < asIndex &&
            tokens[kind].IsKeyword("OR") &&
            tokens[kind + 1].IsKeyword("ALTER"))
        {
            kind += 2;
        }

        return kind < asIndex &&
            tokens[kind].Kind == SqlTokenKind.Identifier &&
            !tokens[kind].IsQuoted &&
            ModuleKinds.Contains(tokens[kind].Value);
    }

    /// <summary>這個 AS 是標頭本身的一部分：<c>EXECUTE AS</c> 或參數的 <c>@a AS int</c>。</summary>
    private bool BelongsToModuleHeader(int asIndex)
    {
        if (asIndex < 1)
        {
            return false;
        }

        var previous = tokens[asIndex - 1];

        return previous.Kind == SqlTokenKind.Variable ||
            previous.IsKeyword("EXECUTE") ||
            previous.IsKeyword("EXEC");
    }

    /// <summary>
    /// 游標剛好在一整組括號之後。
    /// </summary>
    /// <remarks>
    /// 括號是什麼由它前面那個字決定，而不是由裡面的內容決定：
    /// <c>FROM (SELECT …)</c> 是衍生資料表，<c>WHERE x IN (SELECT …)</c>
    /// 是運算式，兩者裡面裝的是同一個東西。
    ///
    /// 文法強制別名的括號之後那一格一定是名字，寫完之後才是資料來源尾端：衍生資料表
    /// （<c>FROM (SELECT 1)</c> 直接是語法錯誤）與 <c>PIVOT (…)</c>、<c>UNPIVOT (…)</c>。
    /// 其餘情形這一整組括號只是一個算完的運算元，跳過它，位置由更前面的子句關鍵字決定。
    /// </remarks>
    private SqlCaretPosition AfterGroup(int close)
    {
        var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, close);

        if (open < 0)
        {
            // 括號配不起來，前面的文字說明不了這裡是什麼位置。
            return new SqlCaretPosition(SqlKeywordPosition.Any);
        }

        if (RequiresAlias(open))
        {
            return new SqlCaretPosition(SqlKeywordPosition.TableSourceTail, SqlCompletionSlot.Name);
        }

        // CTE 寫完之後是它自己那一句的開頭：SELECT、INSERT、UPDATE、DELETE、MERGE。
        if (EndsCommonTableExpression(close))
        {
            return new SqlCaretPosition(SqlKeywordPosition.StatementStart);
        }

        // 敘述自己的格子也以一整組括號寫完：MERGE 的 VALUES (…)、GRANT SELECT (a, b)。
        if (FindStatementSlot(close) is { } slot)
        {
            return new SqlCaretPosition(slot);
        }

        // STRING_AGG(…) WITHIN GROUP (ORDER BY …) 與前面那次呼叫是一個單位，之後照那次呼叫：還接得了 OVER。
        // 不跳過的話 GROUP 會被當成 GROUP BY 的錨點。
        if (open >= 3 && tokens[open - 1].IsKeyword("GROUP") && tokens[open - 2].IsKeyword("WITHIN") &&
            tokens[open - 3].IsPunctuation(")"))
        {
            return AfterGroup(open - 3);
        }

        var position = FindClausePosition(open - 1);

        // 函式呼叫寫完之後多接 OVER：只在選取清單與 ORDER BY 的尾端，視窗函式只寫得在那裡。
        // 限定的名稱（dbo.fn_Fee）是使用者定義函式，接不了 OVER。
        if (open >= 1 && IsPlainWord(open - 1) && !(open >= 2 && tokens[open - 2].IsPunctuation(".")) &&
            (position & (SqlKeywordPosition.SelectListTail | SqlKeywordPosition.OrderByTail)) != SqlKeywordPosition.None)
        {
            position |= SqlKeywordPosition.FunctionCallTail;
        }

        return new SqlCaretPosition(position);
    }

    /// <summary><paramref name="open"/> 開啟的那一組括號之後，文法強制要寫別名。</summary>
    private bool RequiresAlias(int open)
    {
        if (open < 1 || !IsBareKeyword(open - 1))
        {
            return false;
        }

        var before = tokens[open - 1];

        return before.IsKeyword("PIVOT") ||
            before.IsKeyword("UNPIVOT") ||
            (TableSourceKeywords.Contains(before.Value) && SqlTokenNavigator.OpensQuery(tokens, open));
    }

    /// <summary>往回找最近的子句關鍵字，取它的「子句尾端」位置。</summary>
    private SqlKeywordPosition FindClausePosition(int from)
    {
        return FindAnchorPosition(from, ClauseAnchors);
    }

    /// <summary>
    /// 往回找最近的子句關鍵字，並以 <paramref name="anchors"/> 換成位置。
    /// </summary>
    /// <param name="anchors">
    /// ORDER BY／GROUP BY 的錨點是 <c>BY</c>，以兩個字的鍵查：子句尾端分得出兩者
    /// （ASC／DESC 與 HAVING 各屬一邊），清單起點兩者都是欄位本身。
    /// </param>
    /// <remarks>
    /// 途中遇到右括號一律跳到配對的左括號之前：那一整組是一個運算元，
    /// 裡面的子句屬於它自己。配不起來的左括號（使用者才剛打開、還沒關上的那個）
    /// 則照樣穿過去——<c>SELECT COUNT(a, </c> 的位置仍然由外層的 SELECT 決定。
    ///
    /// 跳過的括號兩兩不重疊，所以整趟仍然是線性的，不會因為括號多就退化。
    ///
    /// <c>CASE … END</c> 是同一件事的第二次：寫完的那一組整組跳過。還沒寫完的 CASE
    /// 則是游標所在的那一層——<c>SELECT CASE WHEN a = 1 THEN b </c> 之後接的是
    /// WHEN、ELSE、END，不是 FROM。位置由往回遇到的第一個 WHEN／THEN／ELSE 決定。
    /// WHEN 的條件不借用述詞尾端：那會帶進 WHERE、GROUP、UNION 這些 CASE 裡寫不出的
    /// 子句字；IN、LIKE、THEN、AND 由產生器的樣板直接分到 CaseArm。
    /// 穿過沒關上的左括號之後就不再認：那時游標在括號裡的另一個運算式，
    /// 外層 CASE 走到哪一段與它無關，照舊由子句錨點決定。
    ///
    /// 錨點只在游標所在的這一句裡找：分號、GO 與這一句的開頭（<see cref="IsStatementHead"/>）
    /// 之前是別的敘述。走到那裡還沒有錨點，就是這一句自己沒有子句關鍵字（<c>EXEC</c>、
    /// <c>PRINT</c>、<c>RETURN</c>），判不出來，不借上一句的——<c>WHERE b = 1⏎EXEC p @x </c>
    /// 借到 WHERE 的話 OUTPUT 就不見了。
    ///
    /// <c>IF</c>、<c>WHILE</c> 的錨點帶著語句開頭，只有它們自己是一句的開頭時才算
    /// （<c>DROP TABLE IF EXISTS</c> 的 IF 不是）；穿過沒關上的左括號（<c>IF (@a = 1 </c>）
    /// 時還在條件裡面，拿掉語句開頭。
    /// </remarks>
    private SqlKeywordPosition FindAnchorPosition(
        int from,
        Dictionary<string, SqlKeywordPosition> anchors)
    {
        SqlKeywordPosition? caseArm = null;
        var insideGroup = false;

        for (var index = from; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (open < 0)
                {
                    return SqlKeywordPosition.Any;
                }

                index = open;
                continue;
            }

            if (token.IsPunctuation(";") || token.IsKeyword("GO"))
            {
                return SqlKeywordPosition.Any;
            }

            if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
            {
                insideGroup |= token.IsPunctuation("(");
                continue;
            }

            if (token.IsKeyword("END") && FindCaseStart(index) is var caseStart and >= 0)
            {
                index = caseStart;
                continue;
            }

            if (!insideGroup)
            {
                if (caseArm is null && token.IsKeyword("WHEN"))
                {
                    caseArm = SqlKeywordPosition.CaseArm;
                }
                else if (caseArm is null && (token.IsKeyword("THEN") || token.IsKeyword("ELSE")))
                {
                    caseArm = SqlKeywordPosition.CaseBody;
                }

                // 簡單 CASE 的 CASE a 之後還沒有任何一段，接的是 WHEN。
                if (token.IsKeyword("CASE"))
                {
                    return caseArm ?? SqlKeywordPosition.CaseBody;
                }
            }

            if (IsOrderOrGroupBy(index))
            {
                // 視窗 OVER (… ORDER BY a 的排序項之後接視窗框架，不接查詢的 OFFSET、UNION。
                if (anchors == ClauseAnchors && !insideGroup && OrdersWindow(index - 1))
                {
                    return SqlKeywordPosition.WindowOrderTail;
                }

                return anchors[tokens[index - 1].IsKeyword("ORDER") ? OrderBy : GroupBy];
            }

            if (IsDmlOutput(index))
            {
                var output = anchors == ClauseAnchors
                    ? SqlKeywordPosition.SelectListTail | FindClausePosition(index - 1)
                    : SqlKeywordPosition.SelectList;
                return insideGroup ? output & ~SqlKeywordPosition.StatementStart : output;
            }

            if (anchors.TryGetValue(token.Value, out var position) &&
                ((position & SqlKeywordPosition.StatementStart) == SqlKeywordPosition.None || IsStatementHead(index)))
            {
                if (RefineAnchor(index, clauseEnd: anchors == ClauseAnchors) is { } refined)
                {
                    if (refined == SqlKeywordPosition.Any)
                    {
                        return refined;
                    }

                    position = refined;
                }

                return insideGroup ? position & ~SqlKeywordPosition.StatementStart : position;
            }

            if (IsStatementHead(index))
            {
                return SqlKeywordPosition.Any;
            }
        }

        return SqlKeywordPosition.Any;
    }

    /// <summary>
    /// 同一個子句錨點屬於不同敘述時，子句寫完之後接的字不同：<c>SELECT … INTO #t </c> 接 FROM，
    /// <c>INSERT INTO t </c> 不接；<c>FETCH NEXT FROM c </c> 接 INTO，查詢的 FROM 不接。
    /// </summary>
    /// <param name="clauseEnd">問的是子句尾端（<see cref="ClauseAnchors"/>），不是清單的起點。</param>
    /// <remarks>
    /// 不接資料來源的 FROM、INTO（<see cref="IntroducesDataSource"/>）不是資料來源清單：尾端與逗號之後
    /// 都判不出位置，<c>RESTORE … FROM DISK = 'a', </c>、<c>FETCH c INTO @a, </c> 不能列出資料表。
    /// </remarks>
    private SqlKeywordPosition? RefineAnchor(int anchor, bool clauseEnd)
    {
        var token = tokens[anchor];

        if ((token.IsKeyword("FROM") || token.IsKeyword("INTO")) && !IntroducesDataSource(anchor))
        {
            return clauseEnd && IntroducesCursor(anchor) ? SqlKeywordPosition.FetchTail : SqlKeywordPosition.Any;
        }

        if (clauseEnd && token.IsKeyword("INTO") && IsSelectInto(anchor))
        {
            return SqlKeywordPosition.SelectIntoTail;
        }

        // OUTPUT … INTO @t 寫完之後回到那句 DML：UPDATE 還接得了 FROM、WHERE。
        if (clauseEnd && token.IsKeyword("INTO") && FindDmlOutput(anchor - 1) is var output and >= 0)
        {
            return SqlKeywordPosition.TableSourceTail | FindClausePosition(output - 1);
        }

        return null;
    }

    /// <summary>
    /// <paramref name="index"/> 是 DML 的 <c>OUTPUT</c> 子句：所屬的動詞是 INSERT、UPDATE（含它的 SET）、
    /// DELETE 或 MERGE。
    /// </summary>
    /// <remarks>
    /// 同一個字也是參數的修飾（<c>@Count int OUTPUT</c>、<c>EXEC p @x = @y OUTPUT</c>），那裡的動詞是
    /// CREATE、ALTER、EXEC，後面不是選取清單。
    ///
    /// OUTPUT 的清單是一份選取清單：開頭與逗號之後是運算式，一項寫完接 <c>AS</c>、<c>INTO</c>，以及那句 DML
    /// 在 OUTPUT 之前就接得了的字（UPDATE 的 FROM、WHERE，INSERT 的 VALUES）。只認子句錨點的話，
    /// UPDATE 的 OUTPUT 清單借到 SET：一項寫完沒有 AS、INTO，逗號之後還當成指派、只列目標的資料行。
    /// </remarks>
    private bool IsDmlOutput(int index)
    {
        if (!tokens[index].IsKeyword("OUTPUT") || !IsBareKeyword(index) || FindVerb(index - 1) is not (var verb and >= 0))
        {
            return false;
        }

        var token = tokens[verb];

        return token.IsKeyword("INSERT") ||
            token.IsKeyword("UPDATE") ||
            token.IsKeyword("DELETE") ||
            token.IsKeyword("MERGE") ||
            (token.IsKeyword("SET") && !IntroducesOptions(verb));
    }

    /// <summary>
    /// 往回、同一層裡 <paramref name="from"/> 所屬的 DML <c>OUTPUT</c>；遇到動詞或 OUTPUT 之後的子句之前沒有就是 -1。
    /// </summary>
    /// <remarks>
    /// OUTPUT 子句寫完之後接的子句（UPDATE、DELETE 的 FROM、WHERE、OPTION，INSERT 的 VALUES、DEFAULT VALUES）
    /// 不屬於它：<c>UPDATE … OUTPUT … FROM </c> 之後的 inserted 不是 OUTPUT 那一份。
    /// </remarks>
    internal int FindDmlOutput(int from)
    {
        for (var index = from; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return -1;
                }

                continue;
            }

            // 沒關上的函式括號還在清單裡（OUTPUT COMPRESS(deleted.|），開啟查詢的才是別的範圍。
            if (token.IsPunctuation(";") ||
                (token.IsPunctuation("(") && SqlTokenNavigator.OpensQuery(tokens, index)) ||
                IsVerbCandidate(index) ||
                EndsOutputClause(index))
            {
                return -1;
            }

            if (IsDmlOutput(index))
            {
                return index;
            }
        }

        return -1;
    }

    private bool EndsOutputClause(int index) =>
        IsBareKeyword(index) &&
        (tokens[index].IsKeyword("FROM") ||
            tokens[index].IsKeyword("WHERE") ||
            tokens[index].IsKeyword("OPTION") ||
            tokens[index].IsKeyword("VALUES") ||
            tokens[index].IsKeyword("DEFAULT"));

    /// <summary><paramref name="order"/> 的 ORDER 在視窗 <c>OVER (</c> 裡。</summary>
    private bool OrdersWindow(int order)
    {
        var open = FindUnclosedParenthesis(order - 1);
        return open >= 1 && tokens[open - 1].IsKeyword("OVER");
    }

    /// <summary><paramref name="index"/> 是不是 ORDER BY／GROUP BY 的那個 BY。</summary>
    private bool IsOrderOrGroupBy(int index)
    {
        if (index < 1 || !tokens[index].IsKeyword("BY"))
        {
            return false;
        }

        var previous = tokens[index - 1];
        return previous.IsKeyword("ORDER") || previous.IsKeyword("GROUP");
    }
}
