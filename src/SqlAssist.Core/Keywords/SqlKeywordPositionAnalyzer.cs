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
/// </remarks>
public static class SqlKeywordPositionAnalyzer
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
    /// <c>WHERE</c>、<c>FROM</c>、<c>OUTPUT</c>、<c>OPTION</c>，而那一整組字掛的是
    /// <see cref="SqlKeywordPosition.TableSourceTail"/>——只給述詞尾端的症狀就是
    /// <c>UPDATE</c> 寫到一半打不出 <c>WHERE</c>。SET 選項（<c>SET NOCOUNT </c>、
    /// <c>SET NOCOUNT ON </c>）不走這裡，見 <see cref="FindSetOptionPart"/>。
    ///
    /// <c>MERGE</c> 與 <c>USING</c> 的目標和來源與 FROM 的資料來源同一種東西：
    /// 寫完之後同樣接得了別名，接著是 USING、ON。
    ///
    /// ORDER BY 與 GROUP BY 以兩個字的形式出現：錨點其實是 <c>BY</c>，要看前一個字才分得出
    /// 是哪一個子句，見 <see cref="FindAnchorPosition"/>。
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
            ["SET"] = SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.TableSourceTail,

            [OrderBy] = SqlKeywordPosition.OrderByTail,
            [GroupBy] = SqlKeywordPosition.GroupByTail
        };

    /// <summary>
    /// 逗號之後回到子句的起點；<see cref="ClauseAnchors"/> 認得而這裡沒有的，
    /// 一律不猜。
    /// </summary>
    /// <remarks>
    /// 不直接沿用 <see cref="AfterKeyword"/>：<c>SET</c> 在那裡是
    /// <c>SET ROWCOUNT</c> 的位置，而 <c>UPDATE t SET a = 1, </c> 之後要的是資料行，
    /// 兩者只是剛好同一個字。
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

    /// <summary><c>CREATE</c> 之後接這些種類時，下一格是使用者正要取的新名字。</summary>
    /// <remarks>
    /// 只收「種類後面直接就是名稱」的：<c>CREATE DEFAULT</c>、<c>CREATE RULE</c> 這種
    /// 已淘汰的寫法不收，少收的代價只是那一格照舊有清單。
    /// </remarks>
    private static readonly HashSet<string> CreatedObjectKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "PROCEDURE", "PROC", "FUNCTION", "VIEW", "TABLE", "TRIGGER", "TYPE", "SCHEMA",
            "INDEX", "SEQUENCE", "SYNONYM", "DATABASE", "STATISTICS", "ROLE", "USER", "LOGIN"
        };

    /// <summary>標頭以 <c>AS</c> 結束、後面接主體的物件種類。</summary>
    private static readonly HashSet<string> ModuleKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "VIEW"
        };

    /// <summary><c>CREATE</c> 與 <c>INDEX</c> 之間可以夾的字。</summary>
    private static readonly HashSet<string> IndexModifiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "UNIQUE", "CLUSTERED", "NONCLUSTERED", "COLUMNSTORE", "XML", "SPATIAL", "PRIMARY"
        };

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

        var caret = AnalyzeClause(tokens, textBeforeToken);
        var phrase = SqlClausePhraseCatalog.Match(tokens, textBeforeToken);

        return new SqlCaretPosition(caret.Keywords, caret.Slot, phrase, StartsBatch(tokens));
    }

    /// <summary>同一個批次裡游標前面還沒有任何詞元。</summary>
    /// <remarks>
    /// 詞法分析只把 ScriptDom 判定的批次分隔回報成 <c>GO</c> 關鍵字，名為 GO 的欄位不算。
    /// </remarks>
    private static bool StartsBatch(IReadOnlyList<SqlToken> tokens)
    {
        return tokens.Count == 0 || tokens[tokens.Count - 1].IsKeyword("GO");
    }

    /// <summary>
    /// <paramref name="index"/> 的詞元前面那一格是什麼位置。
    /// </summary>
    /// <remarks>
    /// 子句片語的 <see cref="SqlClausePhrase.After"/> 問的就是這個：<c>UPDATE t SET </c> 的 SET
    /// 前面是資料來源尾端，接的是資料行，不是工作階段選項。判法與游標處的位置分析同一條，
    /// 子句寫完又換了行時一樣補上語句開頭。
    /// </remarks>
    internal static SqlKeywordPosition PositionBefore(IReadOnlyList<SqlToken> tokens, int index, string textBeforeToken)
    {
        if (index <= 0)
        {
            return SqlKeywordPosition.StatementStart;
        }

        var before = AnalyzeAt(tokens, index - 1, followAlias: true).Keywords;
        return AddStatementStartOnNewLine(before, tokens, index - 1, tokens[index].Start, textBeforeToken);
    }

    private static SqlCaretPosition AnalyzeClause(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
    {
        var last = tokens.Count - 1;
        var caret = AnalyzeAt(tokens, last, followAlias: true);

        // 名字那一格前面的換行不代表下一句開始了：名字本身還沒寫。
        if (caret.Slot != SqlCompletionSlot.Grammar)
        {
            return caret;
        }

        // 沒有 AS 的別名可能是名字。這一支放在最後而不是併進 AnalyzeAt：
        // 它要看的是原文裡的換行，而詞元串流沒有那個資訊。別名寫完之後接的位置
        // 與不寫別名時一樣（選取清單尾端、資料來源尾端），所以關鍵字位置原樣留著——
        // SELECT PublCode FR 仍然列得出 FROM。
        if (StaysOnSameLine(tokens, textBeforeToken) && IsUnaliasedItem(tokens, last, caret.Keywords))
        {
            return new SqlCaretPosition(caret.Keywords, SqlCompletionSlot.MaybeName);
        }

        return new SqlCaretPosition(
            AddStatementStartOnNewLine(caret.Keywords, tokens, last, textBeforeToken.Length, textBeforeToken));
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
    /// </remarks>
    /// <param name="last">子句最後一個詞元。</param>
    /// <param name="gapEnd">換行要落在 <paramref name="last"/> 之後、這個位置之前。</param>
    private static SqlKeywordPosition AddStatementStartOnNewLine(
        SqlKeywordPosition position,
        IReadOnlyList<SqlToken> tokens,
        int last,
        int gapEnd,
        string textBeforeToken)
    {
        if ((position & SqlKeywordPosition.StatementStart) != SqlKeywordPosition.None ||
            (position & StatementEndPositions) == SqlKeywordPosition.None ||
            last < 0 ||
            !StartsOnNewLine(tokens[last].End, gapEnd, textBeforeToken))
        {
            return position;
        }

        // 函式引數、子查詢與 CTE 還在括號內時，換行不代表可以開始獨立敘述。
        return SqlTokenNavigator.FindUnclosedParenthesis(tokens, last) < 0
            ? position | SqlKeywordPosition.StatementStart
            : position;
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
    private static bool StaysOnSameLine(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
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
    /// 運算元的結尾是識別字（含多段名稱）、變數、右括號、字串與數值常值，以及
    /// <c>CASE … END</c> 的 <c>END</c>；<c>*</c> 不算——<c>SELECT * </c> 與
    /// <c>SELECT t.* </c> 後面不能接別名。
    ///
    /// 兩種清單的差別只在「一項」長什麼樣：資料來源是一個名稱或一次資料表值函式
    /// 呼叫，前面必須直接是 FROM／JOIN／APPLY／USING 或 FROM 清單的逗號；選取清單
    /// 的一項是一整個運算式（<c>a + b</c>、<c>COUNT(*)</c>），往回走到 SELECT、
    /// 逗號或 TOP 子句才算數，途中穿過沒關上的括號或 CASE 就不是清單這一層。
    /// </remarks>
    private static bool IsUnaliasedItem(
        IReadOnlyList<SqlToken> tokens,
        int last,
        SqlKeywordPosition keywords)
    {
        var dataSource = keywords == SqlKeywordPosition.TableSourceTail;

        if (!dataSource && keywords != SqlKeywordPosition.SelectListTail)
        {
            return false;
        }

        var start = FindOperandStart(tokens, last, dataSource);

        if (start < 1)
        {
            return false;
        }

        return dataSource
            ? OpensDataSource(tokens, start - 1)
            : OpensSelectItem(tokens, start - 1);
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
    private static int FindOperandStart(IReadOnlyList<SqlToken> tokens, int last, bool dataSource)
    {
        if (dataSource && FindTemporalClause(tokens, last) is var temporal and >= 1)
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
                (dataSource || !IsBareKeyword(tokens, name)))
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
            case SqlTokenKind.Identifier when !IsBareKeyword(tokens, last):
                return SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last);
            case SqlTokenKind.Identifier when !dataSource && token.IsKeyword("END"):
                return FindCaseStart(tokens, last);
            default:
                return -1;
        }
    }

    /// <summary>
    /// 沒加引號、而且是關鍵字的識別字；點號後面那一段是名稱，不算。
    /// </summary>
    private static bool IsBareKeyword(IReadOnlyList<SqlToken> tokens, int index)
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
    /// DELETE 與 FETCH 自己的 FROM 不算，見 <see cref="NamesStatementTarget"/>。
    /// </remarks>
    private static bool OpensDataSource(IReadOnlyList<SqlToken> tokens, int previous)
    {
        var token = tokens[previous];

        if (token.Kind == SqlTokenKind.Identifier &&
            !token.IsQuoted &&
            (TableSourceKeywords.Contains(token.Value) ||
             token.IsKeyword("MERGE") ||
             (token.IsKeyword("INTO") && previous >= 1 && tokens[previous - 1].IsKeyword("MERGE"))))
        {
            return !token.IsKeyword("FROM") || !NamesStatementTarget(tokens, previous);
        }

        // FROM a, b | 的逗號也開啟一個資料來源，但 SELECT a, b | 的不是。
        return token.IsPunctuation(",")
            && FindAnchorPosition(tokens, previous - 1, ListAnchors) == SqlKeywordPosition.DataSource;
    }

    /// <summary><paramref name="from"/> 的 FROM 帶出的是 DELETE 或 FETCH 自己的目標。</summary>
    /// <remarks>
    /// <c>DELETE [TOP (5)] FROM t</c> 與 <c>FETCH NEXT FROM c</c> 的 FROM 後面是動詞的目標，
    /// 文法不接別名；<c>DELETE a FROM t a JOIN …</c> 的第二個 FROM 才是一般的資料來源。
    /// 往回走到這一句的動詞：FETCH 只有一個 FROM；DELETE 的 FROM 前面還沒寫目標名稱時就是目標。
    /// 途中的括號整組跳過（<c>TOP (5)</c>），數值與變數是 FETCH 的位移（<c>ABSOLUTE @n</c>）。
    /// </remarks>
    private static bool NamesStatementTarget(IReadOnlyList<SqlToken> tokens, int from)
    {
        var named = false;

        for (var index = from - 1; index >= 0; index--)
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

            if (token.Kind is SqlTokenKind.Number or SqlTokenKind.Variable)
            {
                continue;
            }

            if (token.Kind != SqlTokenKind.Identifier)
            {
                return false;
            }

            if (!IsBareKeyword(tokens, index))
            {
                named = true;
                continue;
            }

            if (StartsStatement(token))
            {
                return token.IsKeyword("FETCH") || (token.IsKeyword("DELETE") && !named);
            }
        }

        return false;
    }

    /// <summary>這個關鍵字能開始一句。</summary>
    private static bool StartsStatement(SqlToken keyword) =>
        (SqlKeywordCatalog.GetPositions(keyword.Value) & SqlKeywordPosition.StatementStart) != SqlKeywordPosition.None;

    /// <summary>
    /// 最後一個運算元前面是 <paramref name="previous"/>，而它屬於選取清單這一層的
    /// 同一項，而且那一項還沒有別名。
    /// </summary>
    private static bool OpensSelectItem(IReadOnlyList<SqlToken> tokens, int previous)
    {
        // 緊鄰另一個運算元或 AS：別名已經寫了。TOP 的引數例外，它不是清單的一項。
        if (tokens[previous].IsKeyword("AS") ||
            (FindOperandStart(tokens, previous, dataSource: false) >= 0 &&
             !EndsTopClause(tokens, previous)))
        {
            return false;
        }

        for (var index = previous; index >= 0; index--)
        {
            var token = tokens[index];

            if (EndsTopClause(tokens, index) ||
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
                index = FindCaseStart(tokens, index);

                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            // 沒關上的括號是函式引數或子查詢的裡面；CASE 的各段是 CASE 的裡面。
            // 兩者都不是清單這一層，後面接的不會是別名。
            if (token.IsPunctuation("(") ||
                token.IsPunctuation(";") ||
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
    private static bool EndsTopClause(IReadOnlyList<SqlToken> tokens, int last)
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
    private static int FindTemporalClause(IReadOnlyList<SqlToken> tokens, int last)
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
    /// END 也收 <c>BEGIN … END</c> 與 <c>BEGIN TRY … END TRY</c>。往回數的路上遇到
    /// BEGIN、分號或 GO 就代表這不是 CASE 的：CASE 是運算式，裡面寫不出那三個東西。
    /// 括號整組跳過，理由與 <see cref="FindAnchorPosition"/> 相同。
    /// </remarks>
    private static int FindCaseStart(IReadOnlyList<SqlToken> tokens, int end)
    {
        if (end + 1 < tokens.Count &&
            (tokens[end + 1].IsKeyword("TRY") || tokens[end + 1].IsKeyword("CATCH")))
        {
            return -1;
        }

        var depth = 0;

        for (var index = end; index >= 0; index--)
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

            if (token.IsPunctuation(";") || token.IsKeyword("BEGIN") || token.IsKeyword("GO"))
            {
                return -1;
            }

            if (token.IsKeyword("END"))
            {
                depth++;
            }
            else if (token.IsKeyword("CASE") && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// <paramref name="last"/> 這一格是使用者正要取的新名字，或可能是。
    /// </summary>
    /// <remarks>
    /// 四種都是「前面的字已經說完這裡要一個還不存在的名稱」：
    ///
    /// <list type="bullet">
    /// <item><c>CREATE PROCEDURE </c>、<c>CREATE UNIQUE INDEX </c> 這類建立敘述的物件名稱。
    /// <c>CREATE OR ALTER</c> 例外：那一格也常是既有物件，所以是「可能是名字」。
    /// <c>ALTER PROCEDURE </c> 要的是既有物件，不在這裡。</item>
    /// <item><c>SELECT … INTO </c> 的目標。<c>INSERT INTO </c>、<c>MERGE INTO </c> 要的是
    /// 既有資料表，<c>FETCH … INTO </c> 與 <c>OUTPUT … INTO </c> 的子句錨點不是 SELECT，
    /// 都不在這裡。</item>
    /// <item>敘述開頭的 <c>WITH </c> 與 <c>WITH c AS (…), </c> 的 CTE 名稱。
    /// 資料表提示與選項的 <c>WITH</c> 前面接的不是敘述的開頭，不受影響。</item>
    /// </list>
    ///
    /// 帶限定字時一樣：<c>CREATE PROCEDURE dbo.</c> 的點號由 <see cref="AnalyzeAt"/>
    /// 剝掉之後回到這裡。
    /// </remarks>
    private static bool TryResolveNewName(
        IReadOnlyList<SqlToken> tokens,
        int last,
        bool followAlias,
        out SqlCaretPosition caret)
    {
        var token = tokens[last];

        if (token.IsPunctuation(",") && ContinuesCommonTableExpressions(tokens, last, followAlias))
        {
            caret = new SqlCaretPosition(SqlKeywordPosition.Any, SqlCompletionSlot.Name);
            return true;
        }

        if (token.IsKeyword("WITH") && OpensCommonTableExpression(tokens, last, followAlias))
        {
            caret = new SqlCaretPosition(SqlKeywordPosition.Any, SqlCompletionSlot.Name);
            return true;
        }

        // 新資料表寫完之後接的是 FROM、WHERE，與 SELECT … INTO 既有的尾端一樣。
        if (token.IsKeyword("INTO") && IsSelectInto(tokens, last))
        {
            caret = new SqlCaretPosition(SqlKeywordPosition.TableSourceTail, SqlCompletionSlot.Name);
            return true;
        }

        if (token.Kind == SqlTokenKind.Identifier &&
            !token.IsQuoted &&
            CreatedObjectKinds.Contains(token.Value) &&
            ClassifyCreatedName(tokens, last) is { } slot)
        {
            caret = new SqlCaretPosition(SqlKeywordPosition.Any, slot);
            return true;
        }

        caret = default;
        return false;
    }

    /// <summary><paramref name="kind"/> 是建立敘述的物件種類時，那個物件名稱是哪一種名字。</summary>
    private static SqlCompletionSlot? ClassifyCreatedName(IReadOnlyList<SqlToken> tokens, int kind)
    {
        var index = kind - 1;

        if (tokens[kind].IsKeyword("INDEX"))
        {
            while (index >= 0 &&
                   tokens[index].Kind == SqlTokenKind.Identifier &&
                   !tokens[index].IsQuoted &&
                   IndexModifiers.Contains(tokens[index].Value))
            {
                index--;
            }
        }

        if (index < 0)
        {
            return null;
        }

        if (tokens[index].IsKeyword("CREATE"))
        {
            return SqlCompletionSlot.Name;
        }

        return index >= 2 &&
            tokens[index].IsKeyword("ALTER") &&
            tokens[index - 1].IsKeyword("OR") &&
            tokens[index - 2].IsKeyword("CREATE")
                ? SqlCompletionSlot.MaybeName
                : null;
    }

    /// <summary><paramref name="into"/> 是 <c>SELECT … INTO</c> 的 INTO。</summary>
    private static bool IsSelectInto(IReadOnlyList<SqlToken> tokens, int into)
    {
        return into >= 1 &&
            !tokens[into - 1].IsKeyword("INSERT") &&
            !tokens[into - 1].IsKeyword("MERGE") &&
            FindClausePosition(tokens, into - 1) == SqlKeywordPosition.SelectListTail;
    }

    /// <summary><paramref name="with"/> 是敘述開頭、開啟 CTE 的那個 WITH。</summary>
    /// <remarks>
    /// CTE 前面的敘述必須以分號結束，所以前面只可能是：什麼都沒有、分號、GO、
    /// <c>BEGIN</c>，或模組主體的 <c>AS</c>（<c>CREATE VIEW v AS WITH …</c>）。
    /// 其餘的 WITH 是資料表提示、索引選項或 <c>WITH SCHEMABINDING</c>——前面都接著一個
    /// 名稱或括號，這裡一律不認。
    ///
    /// <c>AS</c> 要多問一次它是不是別名；那一問會再走一次位置分析，所以只在
    /// <paramref name="followAlias"/> 時問，理由與 <see cref="AnalyzeAt"/> 的同名參數相同。
    /// </remarks>
    private static bool OpensCommonTableExpression(IReadOnlyList<SqlToken> tokens, int with, bool followAlias)
    {
        if (with == 0)
        {
            return true;
        }

        var previous = tokens[with - 1];

        if (previous.IsPunctuation(";") || previous.IsKeyword("GO") || previous.IsKeyword("BEGIN"))
        {
            return true;
        }

        return followAlias &&
            previous.IsKeyword("AS") &&
            !IntroducesAlias(tokens, with - 1, out _);
    }

    /// <summary>
    /// <paramref name="comma"/> 接在一個寫完的 CTE 後面：<c>WITH a AS (…), </c>。
    /// </summary>
    /// <remarks>
    /// 往回認的形狀是 <c>名稱 [(資料行…)] AS (…)</c>，一路認到開頭的 WITH；
    /// 中間每一個逗號都要是同一種形狀。用迴圈而不是遞迴，理由與
    /// <see cref="SqlTokenNavigator.OpensQuery"/> 相同。
    /// </remarks>
    private static bool ContinuesCommonTableExpressions(
        IReadOnlyList<SqlToken> tokens,
        int comma,
        bool followAlias)
    {
        var index = comma;

        while (index >= 1 && tokens[index - 1].IsPunctuation(")"))
        {
            var body = SqlTokenNavigator.FindOpeningParenthesis(tokens, index - 1);

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
                return OpensCommonTableExpression(tokens, name - 1, followAlias);
            }

            if (!tokens[name - 1].IsPunctuation(","))
            {
                return false;
            }

            index = name - 1;
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
    private static bool OpensColumnDefinitions(IReadOnlyList<SqlToken> tokens, int open)
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
    /// 認的是「往回正好是 <c>ALTER TABLE</c> 加一個名稱單位」而不是「這份指令碼裡
    /// 有沒有 ALTER TABLE」，理由與 <c>SqlScopeAnalyzer.IsMergeAction</c> 相同：
    /// 一個 <c>ALTER TABLE</c> 之後接著獨立的敘述時，那個敘述不屬於它。
    ///
    /// <c>ADD COLUMN</c> 不必判——T-SQL 沒有這種寫法，<c>COLUMN</c> 只跟在
    /// <c>ALTER</c> 與 <c>DROP</c> 後面。
    /// </remarks>
    private static bool TryResolveAlterTable(
        IReadOnlyList<SqlToken> tokens,
        int last,
        out SqlKeywordPosition position)
    {
        if (tokens[last].IsKeyword("ADD"))
        {
            position = SqlKeywordPosition.AlterTableAdd;
            return IsAlterTableTarget(tokens, last - 1);
        }

        if (tokens[last].IsKeyword("COLUMN"))
        {
            position = SqlKeywordPosition.AlterTableColumn;
            return last >= 1
                && (tokens[last - 1].IsKeyword("ALTER") || tokens[last - 1].IsKeyword("DROP"))
                && IsAlterTableTarget(tokens, last - 2);
        }

        position = SqlKeywordPosition.AlterTableAction;
        return IsAlterTableTarget(tokens, last);
    }

    /// <summary><paramref name="last"/> 是 <c>ALTER TABLE</c> 目標名稱的最後一個詞元。</summary>
    private static bool IsAlterTableTarget(IReadOnlyList<SqlToken> tokens, int last)
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
    /// 允許為了判斷 <c>AS</c> 是不是別名而再往前看一格。這是唯一的一層遞迴，
    /// 而且只有一層：<c>AS AS</c> 這種寫不出來的東西不該讓分析器把堆疊用完。
    /// </param>
    private static SqlCaretPosition AnalyzeAt(
        IReadOnlyList<SqlToken> tokens,
        int last,
        bool followAlias)
    {
        if (last < 0)
        {
            return new SqlCaretPosition(SqlKeywordPosition.StatementStart);
        }

        // TOP 的引數要排在括號之前問：TOP (10) 的右括號不是一個算完的運算元。
        // 之後仍是選取清單的起點，另外接得了 PERCENT、WITH TIES；WITH TIES 寫完就不再接。
        if (EndsTopClause(tokens, last))
        {
            return new SqlCaretPosition(tokens[last].IsKeyword("TIES")
                ? SqlKeywordPosition.SelectList
                : SqlKeywordPosition.SelectList | SqlKeywordPosition.TopClauseTail);
        }

        // FOR SYSTEM_TIME … 是資料表名稱的後綴，寫完之後的位置與名稱之後相同。
        if (FindTemporalClause(tokens, last) is var temporal and >= 1)
        {
            return AnalyzeAt(tokens, temporal - 1, followAlias);
        }

        var token = tokens[last];

        if (token.IsPunctuation(")"))
        {
            return AfterGroup(tokens, last);
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
                tokens,
                SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last - 1) - 1,
                followAlias);
        }

        // 加引號的識別字是名稱不是關鍵字：[AS] 之後不是別名位置。
        if (followAlias &&
            token.Kind == SqlTokenKind.Identifier &&
            !token.IsQuoted &&
            token.IsKeyword("AS"))
        {
            if (IntroducesAlias(tokens, last, out var afterAlias))
            {
                return new SqlCaretPosition(afterAlias, SqlCompletionSlot.Name);
            }

            return new SqlCaretPosition(OpensModuleBody(tokens, last)
                ? SqlKeywordPosition.StatementStart
                : SqlKeywordPosition.Any);
        }

        if (TryResolveNewName(tokens, last, followAlias, out var named))
        {
            return named;
        }

        // 資料行定義的起點可能是新資料行的名稱，也可能是 CONSTRAINT、PRIMARY KEY——
        // ALTER TABLE t ADD 與 CREATE TABLE t ( 都是這種格子，同一條規則；
        // 兩者接得了的關鍵字不一樣，所以是兩個位置。
        var keywords = KeywordsAfter(tokens, last);

        return new SqlCaretPosition(
            keywords,
            keywords is SqlKeywordPosition.AlterTableAdd or SqlKeywordPosition.ColumnDefinition
                ? SqlCompletionSlot.MaybeName
                : SqlCompletionSlot.Grammar);
    }

    /// <summary>
    /// <paramref name="last"/> 這個詞元之後接得了哪些關鍵字；
    /// 括號、限定字與 <c>AS</c> 由 <see cref="AnalyzeAt"/> 先處理。
    /// </summary>
    private static SqlKeywordPosition KeywordsAfter(IReadOnlyList<SqlToken> tokens, int last)
    {
        var token = tokens[last];

        if (token.IsPunctuation(";"))
        {
            return SqlKeywordPosition.StatementStart;
        }

        // 資料行定義清單的開頭與逗號之後。逗號要先問這一條：往回找清單錨點時會穿過
        // 那個左括號，走到 CREATE 之前的別的子句。
        if ((token.IsPunctuation("(") && OpensColumnDefinitions(tokens, last)) ||
            (token.IsPunctuation(",") &&
             OpensColumnDefinitions(tokens, SqlTokenNavigator.FindUnclosedParenthesis(tokens, last - 1))))
        {
            return SqlKeywordPosition.ColumnDefinition;
        }

        // SELECT a, | 與 FROM a, | 都是清單再來一項，位置回到清單的起點。
        // ORDER BY a, | 也一樣：下一項仍然是欄位。
        if (token.IsPunctuation(","))
        {
            return FindAnchorPosition(tokens, last - 1, ListAnchors);
        }

        if (token.Kind is SqlTokenKind.Punctuation or SqlTokenKind.Operator)
        {
            return SqlKeywordPosition.Any;
        }

        // SET 選項要排在 AfterKeyword 之前：SET ANSI_NULLS ON 的 ON 是選項值，
        // 交給那一條的話會被當成 JOIN 的 ON，下一行打 GO 只剩 GROUPING 這種字。
        switch (FindSetOptionPart(tokens, last))
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
            if (AfterKeyword.TryGetValue(token.Value, out var position))
            {
                return position;
            }

            if (IsOrderOrGroupBy(tokens, last))
            {
                return SqlKeywordPosition.OrderByColumn;
            }

            // ALTER TABLE 的三個位置要排在「認得但沒有對應位置」之前：ADD 與 COLUMN
            // 都是目錄認得的關鍵字，讓那一條先接走的話這裡永遠回 Any。
            if (TryResolveAlterTable(tokens, last, out var alterPosition))
            {
                return alterPosition;
            }

            // CASE … END 是一個算完的運算元，與一整組括號同一個道理。
            if (token.IsKeyword("END") && FindCaseStart(tokens, last) is var caseStart and >= 0)
            {
                return FindClausePosition(tokens, caseStart - 1);
            }

            if (SqlKeywordCatalog.IsKeyword(token.Value))
            {
                // 認得但沒有對應位置的關鍵字（THEN、ELSE…），不猜。
                return SqlKeywordPosition.Any;
            }
        }

        // 變數也走這裡：@x 之後是一個算完的運算元。
        return FindClausePosition(tokens, last);
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
    /// 往回找 SET 時只穿過名稱與值寫得出的詞元，遇到能開始一句的關鍵字就停：
    /// <c>SET NOCOUNT ON SELECT a FROM t </c> 的 <c>t</c> 屬於後面那一句。
    /// SET 本身還要是選項的 SET，見 <see cref="IntroducesOptions"/>。
    /// </remarks>
    private static SetOptionPart FindSetOptionPart(IReadOnlyList<SqlToken> tokens, int last)
    {
        var set = last;

        for (; set >= 0; set--)
        {
            var token = tokens[set];

            if (token.IsKeyword("SET"))
            {
                break;
            }

            if (!CanBelongToSetOption(token) ||
                (token.Kind == SqlTokenKind.Identifier && IsBareKeyword(tokens, set) && StartsStatement(token)))
            {
                return SetOptionPart.None;
            }
        }

        if (set < 0 ||
            set == last ||
            tokens[set + 1].Kind != SqlTokenKind.Identifier ||
            !IntroducesOptions(tokens, set))
        {
            return SetOptionPart.None;
        }

        var nameEnd = set + 1;

        while (nameEnd < last)
        {
            var next = tokens[nameEnd + 1];

            if (next.IsPunctuation(".") ||
                (next.Kind == SqlTokenKind.Identifier && !IsBareKeyword(tokens, nameEnd + 1)))
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

        return IsBareKeyword(tokens, nameEnd) ? SetOptionPart.None : SetOptionPart.Name;
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
    /// 往回找這一句的動詞：第一個能開始陳述式的關鍵字。停在 UPDATE 就是資料行指派
    /// （<c>UPDATE t SET</c>、MERGE 的 <c>THEN UPDATE SET</c>）；停在別的動詞、分號或開頭
    /// 就是選項——<c>UPDATE t SET a = 1 SET NOCOUNT</c> 往回先碰到的是前一個 SET，
    /// 後面那個 SET 因此是新的一句。<c>ALTER DATABASE x SET ANSI_NULLS ON</c> 的 SET
    /// 也是選項：名稱與值的形狀與 SET 敘述相同，值寫完同樣結束這一句。
    ///
    /// <c>AS</c> 也是停點：模組主體從它後面開始，<c>ALTER PROCEDURE p AS SET</c> 與觸發程序的
    /// <c>AFTER UPDATE AS SET</c> 都是一句的開始，而 UPDATE 的 SET 子句前面不會有 AS。
    ///
    /// 一組括號連同緊接在前面的那個字是一個單位：<c>WITH (TABLOCK)</c>、<c>TOP (5)</c>
    /// 屬於 UPDATE 的前段，<c>IF UPDATE(a)</c> 的 UPDATE 是函式，不是動詞。
    /// </remarks>
    private static bool IntroducesOptions(IReadOnlyList<SqlToken> tokens, int setIndex)
    {
        for (var index = setIndex - 1; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (open < 0)
                {
                    return true;
                }

                index = open > 0 && tokens[open - 1].Kind == SqlTokenKind.Identifier ? open - 1 : open;
                continue;
            }

            if (token.IsPunctuation(";"))
            {
                return true;
            }

            if (!IsBareKeyword(tokens, index))
            {
                continue;
            }

            if (token.IsKeyword("AS"))
            {
                return true;
            }

            if (StartsStatement(token))
            {
                return !token.IsKeyword("UPDATE");
            }
        }

        return true;
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
    private static bool IntroducesAlias(
        IReadOnlyList<SqlToken> tokens,
        int asIndex,
        out SqlKeywordPosition afterAlias)
    {
        var before = AnalyzeAt(tokens, asIndex - 1, followAlias: false);
        afterAlias = before.Keywords;

        return before.Slot == SqlCompletionSlot.Name
            || IsUnaliasedItem(tokens, asIndex - 1, before.Keywords);
    }

    /// <summary>
    /// <paramref name="asIndex"/> 的 <c>AS</c> 結束了程序、函式、觸發程序或檢視的標頭，
    /// 後面是主體：一句的開頭。
    /// </summary>
    /// <remarks>
    /// 少了這一條，主體的開頭判不出位置：清單是整份目錄，而 <c>SET NOCOUNT </c> 這種
    /// 子句片語只能以「可能」的身分加字，不能換掉整份清單（見 <see cref="SqlClausePhraseMatch"/>）。
    ///
    /// 往回走到這一句的 CREATE 或 ALTER，看它後面是不是模組種類。途中整組跳過括號
    /// （參數清單、<c>RETURNS @t TABLE (…)</c>）；標頭裡的 AS 只有 <c>EXECUTE AS</c> 與
    /// 參數的 <c>@a AS int</c>，遇到別的 AS、分號、BEGIN、END 或 GO 就是走出了標頭。
    /// </remarks>
    private static bool OpensModuleBody(IReadOnlyList<SqlToken> tokens, int asIndex)
    {
        if (BelongsToModuleHeader(tokens, asIndex))
        {
            return false;
        }

        for (var index = asIndex - 1; index >= 0; index--)
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

            if (token.IsPunctuation(";") || token.IsPunctuation("("))
            {
                return false;
            }

            if (token.Kind != SqlTokenKind.Identifier || !IsBareKeyword(tokens, index))
            {
                continue;
            }

            if (token.IsKeyword("CREATE") || token.IsKeyword("ALTER"))
            {
                return index + 1 < tokens.Count &&
                    tokens[index + 1].Kind == SqlTokenKind.Identifier &&
                    !tokens[index + 1].IsQuoted &&
                    ModuleKinds.Contains(tokens[index + 1].Value);
            }

            if ((token.IsKeyword("AS") && !BelongsToModuleHeader(tokens, index)) ||
                token.IsKeyword("BEGIN") ||
                token.IsKeyword("END") ||
                token.IsKeyword("GO"))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>這個 AS 是標頭本身的一部分：<c>EXECUTE AS</c> 或參數的 <c>@a AS int</c>。</summary>
    private static bool BelongsToModuleHeader(IReadOnlyList<SqlToken> tokens, int asIndex)
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
    private static SqlCaretPosition AfterGroup(IReadOnlyList<SqlToken> tokens, int close)
    {
        var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, close);

        if (open < 0)
        {
            // 括號配不起來，前面的文字說明不了這裡是什麼位置。
            return new SqlCaretPosition(SqlKeywordPosition.Any);
        }

        if (RequiresAlias(tokens, open))
        {
            return new SqlCaretPosition(SqlKeywordPosition.TableSourceTail, SqlCompletionSlot.Name);
        }

        return new SqlCaretPosition(FindClausePosition(tokens, open - 1));
    }

    /// <summary><paramref name="open"/> 開啟的那一組括號之後，文法強制要寫別名。</summary>
    private static bool RequiresAlias(IReadOnlyList<SqlToken> tokens, int open)
    {
        if (open < 1 || !IsBareKeyword(tokens, open - 1))
        {
            return false;
        }

        var before = tokens[open - 1];

        return before.IsKeyword("PIVOT") ||
            before.IsKeyword("UNPIVOT") ||
            (TableSourceKeywords.Contains(before.Value) && SqlTokenNavigator.OpensQuery(tokens, open));
    }

    /// <summary>往回找最近的子句關鍵字，取它的「子句尾端」位置。</summary>
    private static SqlKeywordPosition FindClausePosition(IReadOnlyList<SqlToken> tokens, int from)
    {
        return FindAnchorPosition(tokens, from, ClauseAnchors);
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
    /// </remarks>
    private static SqlKeywordPosition FindAnchorPosition(
        IReadOnlyList<SqlToken> tokens,
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

            if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
            {
                // 分號代表前一個敘述已經結束，再往回找只會找到別的敘述的子句。
                if (token.IsPunctuation(";"))
                {
                    return SqlKeywordPosition.StatementStart;
                }

                insideGroup |= token.IsPunctuation("(");
                continue;
            }

            if (token.IsKeyword("END") && FindCaseStart(tokens, index) is var caseStart and >= 0)
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

            if (IsOrderOrGroupBy(tokens, index))
            {
                return anchors[tokens[index - 1].IsKeyword("ORDER") ? OrderBy : GroupBy];
            }

            if (anchors.TryGetValue(token.Value, out var position))
            {
                return position;
            }
        }

        return SqlKeywordPosition.Any;
    }

    /// <summary><paramref name="index"/> 是不是 ORDER BY／GROUP BY 的那個 BY。</summary>
    private static bool IsOrderOrGroupBy(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (index < 1 || !tokens[index].IsKeyword("BY"))
        {
            return false;
        }

        var previous = tokens[index - 1];
        return previous.IsKeyword("ORDER") || previous.IsKeyword("GROUP");
    }
}
