using System;
using System.Collections.Generic;
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
/// 回傳 <see cref="SqlKeywordPosition.None"/> 是另一回事，見
/// <see cref="Analyze"/>：那代表文法上這裡不接受任何關鍵字。
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
            ["NOT"] = SqlKeywordPosition.Predicate,

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
    /// <c>UPDATE</c> 寫到一半打不出 <c>WHERE</c>。工作階段選項的
    /// <c>SET NOCOUNT ON</c> 會拿到同一組位置，那是「位置分析看到 SET 一律回報
    /// 同一個位置」這個既有取捨的延伸，代價是清單多幾個字。
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

            ["ON"] = SqlKeywordPosition.TableSourceTail | SqlKeywordPosition.ExpressionTail,

            ["WHERE"] = SqlKeywordPosition.ExpressionTail,
            ["HAVING"] = SqlKeywordPosition.ExpressionTail,
            ["SET"] = SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.TableSourceTail
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
            ["HAVING"] = SqlKeywordPosition.Predicate
        };

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
    /// 分析游標所在的位置。
    /// </summary>
    /// <param name="textBeforeToken">
    /// 游標前方的文字，且不含正在輸入的那個詞元——位置由「前一個完整的詞元」決定，
    /// 打到一半的字不算數。
    /// </param>
    /// <returns>
    /// 文法允許的位置；判不出來時是 <see cref="SqlKeywordPosition.Any"/>。
    /// <see cref="SqlKeywordPosition.None"/> 代表這裡<b>不接受任何關鍵字</b>，
    /// 而那一律是因為文法要求的是一個使用者自己取的名字——見
    /// <see cref="AfterGroup"/> 與 <see cref="IntroducesAlias"/>。
    /// </returns>
    public static SqlKeywordPosition Analyze(string textBeforeToken)
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
    public static SqlKeywordPosition Analyze(
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

        var position = AnalyzeAt(tokens, tokens.Count - 1, followAlias: true);

        // 沒有 AS 的別名也是名字。這一支放在最後而不是併進 AnalyzeAt：
        // 它要看的是原文裡的換行，而詞元串流沒有那個資訊。
        if (position == SqlKeywordPosition.TableSourceTail &&
            StaysOnSameLine(textBeforeToken) &&
            IsTableAliasSlot(tokens, tokens.Count - 1))
        {
            return SqlKeywordPosition.None;
        }

        return AddStatementStartOnNewLine(position, tokens, textBeforeToken);
    }

    /// <summary>子句尾端又換了行時，這裡同時也可能是下一個敘述的開頭。</summary>
    private const SqlKeywordPosition ClauseTailPositions =
        SqlKeywordPosition.SelectListTail |
        SqlKeywordPosition.TableSourceTail |
        SqlKeywordPosition.ExpressionTail |
        SqlKeywordPosition.OrderByTail;

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
    private static SqlKeywordPosition AddStatementStartOnNewLine(
        SqlKeywordPosition position,
        IReadOnlyList<SqlToken> tokens,
        string textBeforeToken)
    {
        if ((position & SqlKeywordPosition.StatementStart) != SqlKeywordPosition.None ||
            (position & ClauseTailPositions) == SqlKeywordPosition.None ||
            tokens.Count == 0 ||
            !StartsOnNewLine(tokens[tokens.Count - 1].End, textBeforeToken))
        {
            return position;
        }

        // 函式引數、子查詢與 CTE 還在括號內時，換行不代表可以開始獨立敘述。
        return SqlTokenNavigator.FindUnclosedParenthesis(tokens, tokens.Count - 1) < 0
            ? position | SqlKeywordPosition.StatementStart
            : position;
    }

    /// <summary>游標與前一個詞元之間隔了至少一個換行。</summary>
    private static bool StartsOnNewLine(int previousTokenEnd, string textBeforeToken)
    {
        // 詞法分析已略過註解；直接查看詞元後的間隙，避免區塊註解遮住換行，
        // 也不會把字串或加引號名稱內的換行誤認成敘述邊界。
        for (var index = previousTokenEnd; index < textBeforeToken.Length; index++)
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
    /// 游標與前一個詞元之間沒有換行。
    /// </summary>
    /// <remarks>
    /// 別名一定寫在資料來源的同一行，子句與下一個敘述則幾乎總是換行寫——
    /// 這是唯一分得開「他在取別名」與「他在打 WHERE」的線索，因為兩者在文法上
    /// 都成立，而打到一半的 <c>WHE</c> 與別名在剖析器眼中一模一樣。
    ///
    /// 沒有任何空白時不算：那代表兩個詞元是連著的（<c>t.|</c>），不是別名的位置。
    /// </remarks>
    private static bool StaysOnSameLine(string textBeforeToken)
    {
        var index = textBeforeToken.Length - 1;
        var sawWhitespace = false;

        while (index >= 0 && char.IsWhiteSpace(textBeforeToken[index]))
        {
            if (textBeforeToken[index] == '\n')
            {
                return false;
            }

            sawWhitespace = true;
            index--;
        }

        return sawWhitespace;
    }

    /// <summary>
    /// 游標停在一個資料來源的別名位置上，而且那個別名還沒寫。
    /// </summary>
    /// <remarks>
    /// 判斷方式是往回數「名稱單位」：<c>FROM</c>／<c>JOIN</c>／<c>APPLY</c>／
    /// <c>USING</c>（或 FROM 清單的逗號）與游標之間只有一個名稱，別名就還沒寫。
    /// 兩個就是寫完了——<c>FROM CTE_TEST a </c> 之後接的是 <c>INNER</c>、
    /// <c>WHERE</c>，清單照常。
    ///
    /// 帶點號的名稱算<b>一個</b>單位：<c>dbo.PUBLISHER</c> 是一個資料來源，不是兩個。
    /// </remarks>
    private static bool IsTableAliasSlot(IReadOnlyList<SqlToken> tokens, int last)
    {
        var index = last;

        if (index < 1 ||
            tokens[index].Kind != SqlTokenKind.Identifier ||
            (!tokens[index].IsQuoted && SqlKeywordCatalog.IsKeyword(tokens[index].Value)))
        {
            return false;
        }

        index = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, index);

        if (index < 1)
        {
            return false;
        }

        var previous = tokens[index - 1];

        if (previous.Kind == SqlTokenKind.Identifier &&
            !previous.IsQuoted &&
            TableSourceKeywords.Contains(previous.Value))
        {
            return true;
        }

        // FROM a, b | 的逗號也開啟一個資料來源，但 SELECT a, b | 的不是。
        return previous.IsPunctuation(",")
            && FindAnchorPosition(tokens, index - 2, ListAnchors, SqlKeywordPosition.OrderByColumn)
                == SqlKeywordPosition.DataSource;
    }

    /// <summary>
    /// 游標落在 <c>ALTER TABLE</c> 的三個位置之一。
    /// </summary>
    /// <remarks>
    /// 這三處以前一律回 <see cref="SqlKeywordPosition.Any"/>，代價是 191 個關鍵字與
    /// 45 筆片段全部進場——使用者在 <c>ADD </c> 之後看到的是整個資料庫，而文法上
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
    private static SqlKeywordPosition AnalyzeAt(
        IReadOnlyList<SqlToken> tokens,
        int last,
        bool followAlias)
    {
        if (last < 0)
        {
            return SqlKeywordPosition.StatementStart;
        }

        var token = tokens[last];

        if (token.IsPunctuation(";"))
        {
            return SqlKeywordPosition.StatementStart;
        }

        // SELECT a, | 與 FROM a, | 都是清單再來一項，位置回到清單的起點。
        // ORDER BY a, | 也一樣：下一項仍然是欄位。
        if (token.IsPunctuation(","))
        {
            return FindAnchorPosition(tokens, last - 1, ListAnchors, SqlKeywordPosition.OrderByColumn);
        }

        if (token.IsPunctuation(")"))
        {
            return AfterGroup(tokens, last);
        }

        // 使用者正在打一個變數或參數的名字。那個名字是他自己取的，
        // 而且擴充完全不提供變數名稱——清單裡沒有一項會是對的。
        if (token.Kind == SqlTokenKind.Variable)
        {
            return IsBareAtSign(token.Value)
                ? SqlKeywordPosition.None
                : FindClausePosition(tokens, last);
        }

        if (token.Kind is SqlTokenKind.Punctuation or SqlTokenKind.Operator)
        {
            return SqlKeywordPosition.Any;
        }

        // 加引號的識別字是名稱不是關鍵字：[FROM] 之後不是資料來源位置。
        if (token.Kind == SqlTokenKind.Identifier && !token.IsQuoted)
        {
            if (followAlias && token.IsKeyword("AS"))
            {
                return IntroducesAlias(tokens, last)
                    ? SqlKeywordPosition.None
                    : SqlKeywordPosition.Any;
            }

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

            if (SqlKeywordCatalog.IsKeyword(token.Value))
            {
                // 認得但沒有對應位置的關鍵字（THEN、ELSE…），不猜。
                return SqlKeywordPosition.Any;
            }
        }

        return FindClausePosition(tokens, last);
    }

    /// <summary>
    /// <paramref name="asIndex"/> 的 <c>AS</c> 後面接的是別名，而不是別的東西。
    /// </summary>
    /// <remarks>
    /// <c>AS</c> 在 T-SQL 裡接兩種完全不同的東西，而分辨它們的線索不在後面
    /// （後面還沒打出來）而在前面：
    ///
    /// <list type="bullet">
    /// <item>一個運算式或一個資料來源剛寫完 → 後面是<b>別名</b>：
    /// <c>SELECT x AS </c>、<c>FROM t AS </c>、<c>FROM (SELECT …) AS </c>。</item>
    /// <item>其餘 → 後面不是名字，清單照常：<c>CREATE PROCEDURE p AS </c> 之後
    /// 是主體，<c>CAST(x AS </c> 之後是型別，<c>EXECUTE AS </c> 之後是 USER。</item>
    /// </list>
    ///
    /// 所以問的是同一個問題：<c>AS</c> 那個位置本來是什麼位置。
    /// 選取清單尾端與資料來源尾端代表「一項寫完了」，
    /// <see cref="SqlKeywordPosition.None"/> 則是衍生資料表——它的別名本來就是
    /// 文法強制的，多一個 <c>AS</c> 不改變這件事。
    ///
    /// 比的是<b>整個值相等</b>而不是位元交集：判不出位置時回傳的
    /// <see cref="SqlKeywordPosition.Any"/> 含著上面那兩個旗標，用交集的話
    /// <c>CREATE PROCEDURE p AS </c> 也會被當成別名，主體開頭的 BEGIN、SELECT
    /// 就整組消失——這裡的 fail-open 必須真的 open。
    /// </remarks>
    private static bool IntroducesAlias(IReadOnlyList<SqlToken> tokens, int asIndex)
    {
        return AnalyzeAt(tokens, asIndex - 1, followAlias: false) switch
        {
            SqlKeywordPosition.None => true,
            SqlKeywordPosition.SelectListTail => true,
            SqlKeywordPosition.TableSourceTail => true,
            _ => false
        };
    }

    /// <summary>
    /// 這個變數詞元只有小老鼠，名字還沒打。
    /// </summary>
    /// <remarks>
    /// 位置分析拿到的是「不含正在輸入的那個詞元」的文字，所以 <c>@pub</c> 打到一半時
    /// 這裡看到的是 <c>@</c>。<c>@@</c> 也算：系統函式與變數在這一層分不出來，
    /// 而兩者擴充都不提供。
    /// </remarks>
    private static bool IsBareAtSign(string value)
    {
        foreach (var character in value)
        {
            if (character != '@')
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    /// <summary>
    /// 游標剛好在一整組括號之後。
    /// </summary>
    /// <remarks>
    /// 括號是什麼由它前面那個字決定，而不是由裡面的內容決定：
    /// <c>FROM (SELECT …)</c> 是衍生資料表，<c>WHERE x IN (SELECT …)</c>
    /// 是運算式，兩者裡面裝的是同一個東西。
    ///
    /// 衍生資料表的別名是文法強制的（<c>FROM (SELECT 1)</c> 直接是語法錯誤），
    /// 所以那裡沒有任何關鍵字是對的。其餘情形這一整組括號只是一個算完的運算元，
    /// 跳過它，位置由更前面的子句關鍵字決定。
    /// </remarks>
    private static SqlKeywordPosition AfterGroup(IReadOnlyList<SqlToken> tokens, int close)
    {
        var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, close);

        if (open < 0)
        {
            // 括號配不起來，前面的文字說明不了這裡是什麼位置。
            return SqlKeywordPosition.Any;
        }

        if (open > 0 &&
            SqlTokenNavigator.OpensQuery(tokens, open) &&
            tokens[open - 1].Kind == SqlTokenKind.Identifier &&
            !tokens[open - 1].IsQuoted &&
            TableSourceKeywords.Contains(tokens[open - 1].Value))
        {
            return SqlKeywordPosition.None;
        }

        return FindClausePosition(tokens, open - 1);
    }

    /// <summary>往回找最近的子句關鍵字，取它的「子句尾端」位置。</summary>
    private static SqlKeywordPosition FindClausePosition(IReadOnlyList<SqlToken> tokens, int from)
    {
        return FindAnchorPosition(tokens, from, ClauseAnchors, SqlKeywordPosition.OrderByTail);
    }

    /// <summary>
    /// 往回找最近的子句關鍵字，並以 <paramref name="anchors"/> 換成位置。
    /// </summary>
    /// <param name="orderByPosition">
    /// 錨點是 ORDER BY／GROUP BY 的 BY 時用的位置。子句尾端問的是欄位<b>之後</b>
    /// （<see cref="SqlKeywordPosition.OrderByTail"/>，也就是 ASC／DESC），
    /// 清單起點問的是欄位本身（<see cref="SqlKeywordPosition.OrderByColumn"/>）。
    /// </param>
    /// <remarks>
    /// 途中遇到右括號一律跳到配對的左括號之前：那一整組是一個運算元，
    /// 裡面的子句屬於它自己。配不起來的左括號（使用者才剛打開、還沒關上的那個）
    /// 則照樣穿過去——<c>SELECT COUNT(a, </c> 的位置仍然由外層的 SELECT 決定。
    ///
    /// 跳過的括號兩兩不重疊，所以整趟仍然是線性的，不會因為括號多就退化。
    /// </remarks>
    private static SqlKeywordPosition FindAnchorPosition(
        IReadOnlyList<SqlToken> tokens,
        int from,
        Dictionary<string, SqlKeywordPosition> anchors,
        SqlKeywordPosition orderByPosition)
    {
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

                continue;
            }

            if (IsOrderOrGroupBy(tokens, index))
            {
                return orderByPosition;
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
