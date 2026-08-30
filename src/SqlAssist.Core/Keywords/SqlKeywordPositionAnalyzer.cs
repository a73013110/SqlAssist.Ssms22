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
            ["SET"] = SqlKeywordPosition.ExpressionTail
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

        var tokens = SqlTokenizer.Tokenize(textBeforeToken);

        return AnalyzeAt(tokens, tokens.Count - 1, followAlias: true);
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
        if (token.IsPunctuation(","))
        {
            return FindAnchorPosition(tokens, last - 1, ListAnchors, SqlKeywordPosition.Any);
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
                // ORDER BY | 要的是欄位，不是關鍵字。
                return SqlKeywordPosition.Any;
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
    /// <param name="orderByPosition">錨點是 ORDER BY／GROUP BY 的 BY 時用的位置。</param>
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
