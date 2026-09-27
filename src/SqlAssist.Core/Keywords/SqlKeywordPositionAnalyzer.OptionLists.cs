using System;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

public sealed partial class SqlKeywordPositionAnalyzer
{
    /// <summary>
    /// 敘述自己的選項清單；每一種一筆，形狀相同：往回走過清單、找到錨點、驗證錨點前的標頭。
    /// </summary>
    /// <remarks>
    /// 這幾格接的多半是非關鍵字的選項（<c>LOCAL</c>、<c>ENCRYPTION</c>、<c>COMPRESSION</c>），
    /// 由以位置為鍵的子句片語給；位置判不出來的話片語無從比對，整份目錄全部進場。
    /// 新增一種敘述的選項清單只要加一筆；新的位置照樣要有產生器的樣板與片語。
    /// </remarks>
    private static readonly OptionList[] OptionLists =
    {
        // DECLARE c [SCROLL] CURSOR [LOCAL FAST_FORWARD …]：選項是一串非關鍵字的識別字，不以逗號分隔。
        // DECLARE @c CURSOR 是游標變數，後面不接 FOR，不在這裡。
        new(
            isAnchor: (analyzer, index) => analyzer.IsBareKeyword(index) && analyzer.tokens[index].IsKeyword("CURSOR"),
            isPart: (analyzer, index) => analyzer.IsPlainWord(index),
            endsItem: (_, _) => true,
            header: (analyzer, cursor) => analyzer.DeclaresCursor(cursor)
                ? new OptionSlots(SqlKeywordPosition.CursorOption, SqlKeywordPosition.CursorOption)
                : null,
            separatedByCommas: false),

        // 觸發程序標頭之後的 AFTER|FOR|INSTEAD OF INSERT, UPDATE：DDL 事件（CREATE_TABLE、LOGON）是一般識別字。
        // 排在標頭的 WITH 清單前面：AFTER 是非保留字，WITH 清單會把它當成選項名稱。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("AFTER") ||
                analyzer.tokens[index].IsKeyword("FOR") ||
                (analyzer.tokens[index].IsKeyword("OF") && index >= 1 && analyzer.tokens[index - 1].IsKeyword("INSTEAD")),
            isPart: (analyzer, index) => analyzer.IsPlainWord(index) || analyzer.IsDmlEvent(index),
            endsItem: (_, _) => true,
            header: (analyzer, anchor) =>
                analyzer.EndsTriggerHeader(analyzer.tokens[anchor].IsKeyword("OF") ? anchor - 2 : anchor - 1)
                    ? new OptionSlots(SqlKeywordPosition.TriggerEvent, SqlKeywordPosition.TriggerEventEnd)
                    : null),

        // CREATE|ALTER TRIGGER tr ON t WITH ENCRYPTION, EXECUTE AS 'u'：選項寫完之後是標頭的尾端。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("WITH"),
            isPart: (analyzer, index) => analyzer.IsPlainWord(index) ||
                analyzer.tokens[index].Kind == SqlTokenKind.String ||
                analyzer.IsExecuteAs(index),
            endsItem: (analyzer, index) => analyzer.IsPlainWord(index) || analyzer.tokens[index].Kind == SqlTokenKind.String,
            header: (analyzer, with) => analyzer.IsTriggerTarget(with - 1)
                ? new OptionSlots(SqlKeywordPosition.TriggerOption, SqlKeywordPosition.TriggerHeader)
                : null),

        // GRANT|DENY|REVOKE SELECT, UPDATE (a, b), VIEW DEFINITION：權限寫完之後是 ON、TO、FROM。
        // 權限之後的逗號與 GRANT 本身之後由目錄與片語給，這裡不回位置。WITH GRANT OPTION 的 GRANT 不是開頭。
        new(
            isAnchor: (analyzer, index) => analyzer.OpensPermissionList(index),
            isPart: (analyzer, index) => analyzer.IsPermissionPart(index),
            endsItem: (_, _) => true,
            header: (_, _) => new OptionSlots(null, SqlKeywordPosition.PermissionList),
            skipsGroups: true),

        // GRANT … ON [SCHEMA::]dbo.Loan：目標寫完之後是 TO、FROM。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("ON"),
            isPart: (analyzer, index) => analyzer.IsPlainWord(index) ||
                analyzer.tokens[index].IsPunctuation(".") || analyzer.tokens[index].IsPunctuation("::") ||
                (index + 1 < analyzer.tokens.Count && analyzer.tokens[index + 1].IsPunctuation("::")),
            endsItem: (analyzer, index) => analyzer.IsPlainWord(index),
            header: (analyzer, on) => on >= 1 && analyzer.FindStatementSlot(on - 1) == SqlKeywordPosition.PermissionList
                ? new OptionSlots(null, SqlKeywordPosition.PermissionTarget)
                : null,
            separatedByCommas: false),

        // CREATE|ALTER PROCEDURE|FUNCTION|VIEW … WITH：EXECUTE AS、INLINE = ON、RETURNS NULL ON NULL INPUT。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("WITH"),
            isPart: (analyzer, index) => analyzer.IsModuleOptionPart(index),
            endsItem: (analyzer, index) => analyzer.EndsModuleOption(index),
            header: (analyzer, with) => analyzer.FindModuleOptionHeader(with)),

        // BACKUP|RESTORE DATABASE|LOG … WITH：選項清單不在括號裡，括號裡的逗號走不出那組括號。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("WITH"),
            isPart: (analyzer, index) => !analyzer.StartsClauseOfItsOwn(index),
            endsItem: null,
            header: (analyzer, with) => analyzer.FindBackupHeader(with),
            skipsGroups: true)
    };

    /// <summary>
    /// <paramref name="last"/> 之後是某一種敘述自己的格子時，那個位置；不是就回 null。
    /// </summary>
    /// <remarks>
    /// 每一種都只認「往回緊鄰的形狀」或所屬的動詞，走不出這一句。
    /// </remarks>
    private SqlKeywordPosition? FindStatementSlot(int last)
    {
        if (IsTriggerTarget(last))
        {
            return SqlKeywordPosition.TriggerHeader;
        }

        if (OpensMergeWhen(last))
        {
            return SqlKeywordPosition.MergeWhen;
        }

        if (FindMergeSlot(last) is { } merge)
        {
            return merge;
        }

        if (EndsIndexKey(last))
        {
            return SqlKeywordPosition.IndexKeyTail;
        }

        foreach (var list in OptionLists)
        {
            if (list.Resolve(this, last) is { } position)
            {
                return position;
            }
        }

        return null;
    }

    /// <summary><paramref name="last"/> 寫完觸發程序的標頭：目標，或目標之後的 WITH 選項。</summary>
    private bool EndsTriggerHeader(int last) =>
        last >= 0 && FindStatementSlot(last) == SqlKeywordPosition.TriggerHeader;

    /// <summary>觸發程序的 INSERT、UPDATE、DELETE 事件。</summary>
    private bool IsDmlEvent(int index) =>
        tokens[index].IsKeyword("INSERT") || tokens[index].IsKeyword("UPDATE") || tokens[index].IsKeyword("DELETE");

    /// <summary><paramref name="last"/> 是觸發程序 <c>ON</c> 之後目標名稱的最後一個詞元。</summary>
    /// <remarks>DDL 觸發程序的 <c>ON DATABASE</c> 與 <c>ON ALL SERVER</c> 也算。</remarks>
    private bool IsTriggerTarget(int last)
    {
        if (last < 4 || tokens[last].Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        var on = tokens[last].IsKeyword("SERVER") && tokens[last - 1].IsKeyword("ALL")
            ? last - 2
            : SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last) - 1;

        if (on < 3 || !tokens[on].IsKeyword("ON") || tokens[on - 1].Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        var name = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, on - 1);

        return name >= 2 &&
            tokens[name - 1].IsKeyword("TRIGGER") &&
            (tokens[name - 2].IsKeyword("CREATE") || tokens[name - 2].IsKeyword("ALTER"));
    }

    /// <summary><paramref name="last"/> 是 MERGE 的 <c>WHEN</c>，不是 CASE 的。</summary>
    /// <remarks>
    /// 還沒以 END 收掉的 CASE 裡的 WHEN 是 CASE 的；其餘的 WHEN 屬於這一句，而這一句要以 MERGE 開頭。
    /// 問的是這一句的開頭而不是所屬的動詞：<c>THEN UPDATE SET a = 1 WHEN</c> 的動詞是 SET。
    /// </remarks>
    private bool OpensMergeWhen(int last)
    {
        return tokens[last].IsKeyword("WHEN") &&
            FindUnclosedCase(last - 1) < 0 &&
            tokens[FindStatementStart(last)].IsKeyword("MERGE");
    }

    /// <summary>GRANT、DENY、REVOKE 開始權限清單；<c>WITH GRANT OPTION</c> 的 GRANT 不是。</summary>
    private bool OpensPermissionList(int index) =>
        IsBareKeyword(index) &&
        (tokens[index].IsKeyword("GRANT") || tokens[index].IsKeyword("DENY") || tokens[index].IsKeyword("REVOKE")) &&
        !(index >= 1 && tokens[index - 1].IsKeyword("WITH"));

    /// <summary>
    /// 權限寫得出這個詞元：<c>SELECT</c>、<c>VIEW DEFINITION</c>、<c>ALTER ANY USER</c> 這些字；
    /// 權限清單之後的 ON、TO、FROM 與 WITH 不是。
    /// </summary>
    private bool IsPermissionPart(int index)
    {
        var token = tokens[index];

        return token.Kind == SqlTokenKind.Identifier && !token.IsQuoted &&
            !token.IsKeyword("ON") && !token.IsKeyword("TO") && !token.IsKeyword("FROM") && !token.IsKeyword("WITH");
    }

    /// <summary>
    /// <paramref name="last"/> 之後是 MERGE 自己的格子時，那個位置；不是就回 null。
    /// </summary>
    /// <remarks>
    /// 往回找 MERGE 那一層最近的 ON、WHEN、THEN（整組括號與寫完的 CASE 跳過）：
    /// <list type="bullet">
    /// <item><c>USING s ON t.a = s.a </c>：條件寫完，還能接 AND、OR，也能接 WHEN。</item>
    /// <item><c>WHEN MATCHED AND t.a = 1 </c>：附加條件寫完，與 CASE 的 WHEN 條件同一個位置。</item>
    /// <item><c>THEN </c>：動作；<c>THEN DELETE </c>、<c>THEN UPDATE SET a = 1 </c>、
    /// <c>THEN INSERT (a) VALUES (1) </c>、<c>DEFAULT VALUES </c>：動作寫完。</item>
    /// </list>
    /// 寫到一半的（<c>THEN UPDATE </c>、<c>WHEN MATCHED </c>、<c>ON t.a = </c>）由片語與一般規則回答。
    /// 往回途中遇到能開始一句或開始子句的字就停：那不是 MERGE 這一層，也不必走完整份指令碼。
    /// </remarks>
    private SqlKeywordPosition? FindMergeSlot(int last)
    {
        if (!EndsOperand(last) &&
            !tokens[last].IsKeyword("THEN") && !tokens[last].IsKeyword("DELETE") && !tokens[last].IsKeyword("VALUES"))
        {
            return null;
        }

        var hasAnd = false;

        for (var index = last; index >= 0; index--)
        {
            var token = tokens[index];

            if (token.IsPunctuation(")"))
            {
                index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);

                if (index < 0)
                {
                    return null;
                }

                continue;
            }

            if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
            {
                if (token.IsPunctuation("(") || token.IsPunctuation(";"))
                {
                    return null;
                }

                continue;
            }

            if (token.IsKeyword("END") && FindCaseStart(index) is var caseStart and >= 0)
            {
                index = caseStart;
                continue;
            }

            if (!IsBareKeyword(index))
            {
                continue;
            }

            if (token.IsKeyword("ON"))
            {
                return index < last && FollowsMergeSource(index)
                    ? SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause
                    : null;
            }

            if (token.IsKeyword("WHEN") || token.IsKeyword("THEN"))
            {
                if (FindUnclosedCase(index - 1) >= 0 || !tokens[FindStatementStart(index)].IsKeyword("MERGE"))
                {
                    return null;
                }

                return token.IsKeyword("WHEN")
                    ? (hasAnd && EndsOperand(last) ? SqlKeywordPosition.CaseArm : null)
                    : EndsMergeAction(index, last);
            }

            hasAnd |= token.IsKeyword("AND");

            // 動作的動詞只在 THEN 之後；別的能開始一句或開始子句的字表示這裡不是 MERGE 那一層。
            var verb = token.IsKeyword("UPDATE") || token.IsKeyword("DELETE") || token.IsKeyword("INSERT");

            if ((verb && !(index >= 1 && tokens[index - 1].IsKeyword("THEN"))) ||
                (!verb && !token.IsKeyword("SET") && (StartsStatement(token) || ClauseAnchors.ContainsKey(token.Value))) ||
                token.IsKeyword("USING") || token.IsKeyword("OUTPUT") || token.IsKeyword("OPTION"))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="then"/> 的 THEN 之後、到 <paramref name="last"/> 為止，MERGE 的動作在哪一格。
    /// </summary>
    private SqlKeywordPosition? EndsMergeAction(int then, int last)
    {
        if (then == last)
        {
            return SqlKeywordPosition.MergeAction;
        }

        var verb = tokens[then + 1];

        if (verb.IsKeyword("DELETE"))
        {
            return last == then + 1 ? SqlKeywordPosition.MergeClause : null;
        }

        if (verb.IsKeyword("UPDATE"))
        {
            // SET 之後的指派寫完：值還能接運算子與 COLLATE，也能接下一個 WHEN。
            return then + 2 < last && tokens[then + 2].IsKeyword("SET") && EndsOperand(last)
                ? SqlKeywordPosition.ExpressionTail | SqlKeywordPosition.MergeClause
                : null;
        }

        if (verb.IsKeyword("INSERT"))
        {
            // VALUES (…) 或 DEFAULT VALUES 寫完。
            if (tokens[last].IsKeyword("VALUES"))
            {
                return tokens[last - 1].IsKeyword("DEFAULT") ? SqlKeywordPosition.MergeClause : null;
            }

            var open = tokens[last].IsPunctuation(")") ? SqlTokenNavigator.FindOpeningParenthesis(tokens, last) : -1;

            return open >= 1 && tokens[open - 1].IsKeyword("VALUES") ? SqlKeywordPosition.MergeClause : null;
        }

        return null;
    }

    /// <summary><paramref name="on"/> 的 ON 前面是 MERGE 的 <c>USING 來源 [AS] [別名]</c>。</summary>
    private bool FollowsMergeSource(int on)
    {
        var index = on - 1;

        if (index >= 0 && tokens[index].Kind == SqlTokenKind.Identifier && !IsBareKeyword(index) &&
            index >= 1 && !tokens[index - 1].IsPunctuation(".") && !tokens[index - 1].IsKeyword("USING"))
        {
            index--;
        }

        if (index >= 0 && tokens[index].IsKeyword("AS"))
        {
            index--;
        }

        if (index < 0)
        {
            return false;
        }

        index = tokens[index].IsPunctuation(")")
            ? SqlTokenNavigator.FindOpeningParenthesis(tokens, index) - 1
            : tokens[index].Kind == SqlTokenKind.Identifier
                ? SqlTokenNavigator.SkipQualifiedNameBackward(tokens, index) - 1
                : -1;

        return index >= 0 && tokens[index].IsKeyword("USING");
    }

    /// <summary>
    /// <paramref name="last"/> 寫完一個運算元：名稱、變數、常值、右括號，或 NULL 這種自成一項的關鍵字。
    /// </summary>
    private bool EndsOperand(int last)
    {
        var token = tokens[last];

        return token.Kind switch
        {
            SqlTokenKind.Identifier => !IsBareKeyword(last) || SqlKeywordCatalog.EndsItem(token.Value),
            SqlTokenKind.Punctuation => token.IsPunctuation(")"),
            SqlTokenKind.Operator => false,
            _ => true
        };
    }

    /// <summary>
    /// <paramref name="last"/> 是索引鍵清單裡的一個資料行：CREATE INDEX 的 <c>ON t (a</c>、
    /// <c>PRIMARY KEY (a</c>、<c>UNIQUE (a</c>、內嵌的 <c>INDEX ix (a</c>。
    /// </summary>
    private bool EndsIndexKey(int last)
    {
        if (last < 3 || !IsPlainWord(last) || !(tokens[last - 1].IsPunctuation("(") || tokens[last - 1].IsPunctuation(",")))
        {
            return false;
        }

        var open = tokens[last - 1].IsPunctuation("(") ? last - 1 : SqlTokenNavigator.FindUnclosedParenthesis(tokens, last - 1);

        if (open < 2)
        {
            return false;
        }

        var before = tokens[open - 1];

        if (before.IsKeyword("KEY") || before.IsKeyword("UNIQUE") || before.IsKeyword("CLUSTERED") || before.IsKeyword("NONCLUSTERED"))
        {
            return true;
        }

        if (before.Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        var name = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, open - 1);

        if (name < 1)
        {
            return false;
        }

        // 內嵌索引：INDEX ix (a。
        if (tokens[name - 1].IsKeyword("INDEX"))
        {
            return true;
        }

        // CREATE [UNIQUE] [CLUSTERED] INDEX i ON t (a。
        if (!tokens[name - 1].IsKeyword("ON") || name < 3)
        {
            return false;
        }

        var index = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, name - 2) - 1;

        return index >= 0 && tokens[index].IsKeyword("INDEX");
    }

    /// <summary>不是關鍵字的識別字：選項名稱、游標名稱這一類。</summary>
    private bool IsPlainWord(int index) =>
        tokens[index].Kind == SqlTokenKind.Identifier && !IsBareKeyword(index);

    /// <summary><c>EXECUTE AS</c> 裡的關鍵字。</summary>
    private bool IsExecuteAs(int index) =>
        tokens[index].IsKeyword("EXECUTE") || tokens[index].IsKeyword("EXEC") || tokens[index].IsKeyword("AS");

    /// <summary>
    /// <paramref name="cursor"/> 的 <c>CURSOR</c> 前面是 <c>DECLARE 名稱 [ISO 選項]</c>：兩段各是一串非關鍵字的識別字。
    /// </summary>
    private bool DeclaresCursor(int cursor)
    {
        var declare = cursor - 1;

        while (declare >= 0 && IsPlainWord(declare))
        {
            declare--;
        }

        return declare >= 0 && declare < cursor - 1 && tokens[declare].IsKeyword("DECLARE");
    }

    /// <summary>
    /// 模組選項清單寫得出這個詞元：非關鍵字的名稱、字串、數值、<c>=</c>，以及 <c>EXECUTE AS</c>、
    /// <c>INLINE = ON</c>、<c>RETURNS NULL ON NULL INPUT</c> 裡的關鍵字。
    /// </summary>
    private bool IsModuleOptionPart(int index)
    {
        var token = tokens[index];

        return token.Kind is SqlTokenKind.String or SqlTokenKind.Number ||
            (token.Kind == SqlTokenKind.Operator && token.Value == "=") ||
            IsPlainWord(index) ||
            IsExecuteAs(index) ||
            token.IsKeyword("ON") || token.IsKeyword("OFF") || token.IsKeyword("NULL");
    }

    /// <summary>一個模組選項寫完：名稱、字串或數值，或 <c>= ON</c>、<c>= OFF</c> 的值。</summary>
    private bool EndsModuleOption(int index)
    {
        var token = tokens[index];

        return IsPlainWord(index) ||
            token.Kind is SqlTokenKind.String or SqlTokenKind.Number ||
            ((token.IsKeyword("ON") || token.IsKeyword("OFF")) &&
             index >= 1 && tokens[index - 1].Kind == SqlTokenKind.Operator && tokens[index - 1].Value == "=");
    }

    /// <summary>
    /// <paramref name="with"/> 的 WITH 前面是還沒寫到本體的 <c>CREATE|ALTER PROCEDURE|FUNCTION|VIEW</c> 標頭。
    /// </summary>
    /// <remarks>
    /// 找不到逗號所屬的動詞：<c>EXECUTE AS CALLER, </c> 的 EXECUTE 也能開始一句，所以動詞從 WITH 往回找。
    /// 本體裡 CTE 的 <c>WITH</c> 不算：標頭與 WITH 之間有本體的 AS。
    /// </remarks>
    private OptionSlots? FindModuleOptionHeader(int with)
    {
        if (IsTriggerTarget(with - 1))
        {
            return null;
        }

        var verb = FindVerb(with - 1);

        if (verb < 0 || verb + 1 >= with || !(tokens[verb].IsKeyword("CREATE") || tokens[verb].IsKeyword("ALTER")))
        {
            return null;
        }

        for (var index = verb + 2; index < with; index++)
        {
            // 本體的 AS；參數的 @a AS int 不是。括號整組跳過，函式參數寫在裡面。
            if (tokens[index].IsPunctuation("("))
            {
                index = SqlTokenNavigator.FindClosingParenthesis(tokens, index, with);

                if (index < 0)
                {
                    return null;
                }
            }
            else if (tokens[index].IsKeyword("AS") && tokens[index - 1].Kind != SqlTokenKind.Variable)
            {
                return null;
            }
        }

        var kind = tokens[verb + 1];
        SqlKeywordPosition start;

        if (kind.IsKeyword("PROCEDURE") || kind.IsKeyword("PROC"))
        {
            start = SqlKeywordPosition.ProcedureOption;
        }
        else if (kind.IsKeyword("FUNCTION"))
        {
            start = SqlKeywordPosition.FunctionOption;
        }
        else if (kind.IsKeyword("VIEW"))
        {
            start = SqlKeywordPosition.ViewOption;
        }
        else
        {
            return null;
        }

        return new OptionSlots(start, null);
    }

    /// <summary>
    /// 這個詞元開始另一個子句或另一句：能開始一句的關鍵字、分號、沒關上的左括號。
    /// </summary>
    private bool StartsClauseOfItsOwn(int index)
    {
        var token = tokens[index];

        return token.IsPunctuation(";") || token.IsPunctuation("(") ||
            (token.Kind == SqlTokenKind.Identifier && IsBareKeyword(index) && StartsStatement(token));
    }

    /// <summary>
    /// <paramref name="with"/> 的 WITH 屬於 <c>BACKUP</c>／<c>RESTORE DATABASE|LOG</c>。
    /// </summary>
    /// <remarks>
    /// 只收 DATABASE 與 LOG：<c>BACKUP CERTIFICATE … WITH</c> 接的是別的選項。
    /// </remarks>
    private OptionSlots? FindBackupHeader(int with)
    {
        var verb = FindVerb(with - 1);

        if (verb < 0 || verb + 1 >= with || !(tokens[verb + 1].IsKeyword("DATABASE") || tokens[verb + 1].IsKeyword("LOG")))
        {
            return null;
        }

        return tokens[verb].IsKeyword("BACKUP") ? new OptionSlots(SqlKeywordPosition.BackupOption, null)
            : tokens[verb].IsKeyword("RESTORE") ? new OptionSlots(SqlKeywordPosition.RestoreOption, null)
            : null;
    }

    /// <summary>一種選項清單在一項的開頭與寫完一項之後各是什麼位置；null 是那裡不歸這份清單管。</summary>
    private readonly struct OptionSlots
    {
        public OptionSlots(SqlKeywordPosition? start, SqlKeywordPosition? end)
        {
            Start = start;
            End = end;
        }

        /// <summary>錨點或逗號之後：下一個選項。</summary>
        public SqlKeywordPosition? Start { get; }

        /// <summary>一個選項寫完之後。</summary>
        public SqlKeywordPosition? End { get; }
    }

    /// <summary>
    /// 一種選項清單的形狀：錨點、清單裡寫得出的詞元、錨點前的標頭與對應的位置。
    /// </summary>
    private sealed class OptionList
    {
        private readonly Func<SqlKeywordPositionAnalyzer, int, bool> isAnchor;
        private readonly Func<SqlKeywordPositionAnalyzer, int, bool> isPart;
        private readonly Func<SqlKeywordPositionAnalyzer, int, bool>? endsItem;
        private readonly Func<SqlKeywordPositionAnalyzer, int, OptionSlots?> header;
        private readonly bool separatedByCommas;
        private readonly bool skipsGroups;

        /// <param name="isAnchor">清單從這個詞元之後開始。</param>
        /// <param name="isPart">選項裡寫得出這個詞元；錨點先問，所以同一個字可以兩者都是。</param>
        /// <param name="endsItem">
        /// 一個選項寫到這個詞元已經完整。null 是寫完一項之後不歸這份清單管，那時只在錨點與逗號之後回答，
        /// 也就不必每一次都往回走過整個選項清單。
        /// </param>
        /// <param name="header">錨點前面是這種敘述的標頭時，清單的兩種位置。</param>
        /// <param name="separatedByCommas">選項之間以逗號分隔。</param>
        /// <param name="skipsGroups">選項裡可以有一整組括號，往回時整組跳過。</param>
        public OptionList(
            Func<SqlKeywordPositionAnalyzer, int, bool> isAnchor,
            Func<SqlKeywordPositionAnalyzer, int, bool> isPart,
            Func<SqlKeywordPositionAnalyzer, int, bool>? endsItem,
            Func<SqlKeywordPositionAnalyzer, int, OptionSlots?> header,
            bool separatedByCommas = true,
            bool skipsGroups = false)
        {
            this.isAnchor = isAnchor;
            this.isPart = isPart;
            this.endsItem = endsItem;
            this.header = header;
            this.separatedByCommas = separatedByCommas;
            this.skipsGroups = skipsGroups;
        }

        /// <summary><paramref name="last"/> 之後在這份清單裡的位置；不在清單裡或那裡沒有位置時回 null。</summary>
        /// <remarks>
        /// 錨點與逗號之後是一項的開頭；選項寫到一半（<c>EXECUTE AS </c>）也算開頭——那一格的字由片語給，
        /// 位置只要說得出它還在清單裡。
        /// </remarks>
        public SqlKeywordPosition? Resolve(SqlKeywordPositionAnalyzer analyzer, int last)
        {
            var tokens = analyzer.tokens;
            var atStart = true;
            var index = last;

            if (!isAnchor(analyzer, last))
            {
                if (tokens[last].IsPunctuation(","))
                {
                    if (!separatedByCommas)
                    {
                        return null;
                    }
                }
                else if (endsItem is not null && (isPart(analyzer, last) || (skipsGroups && tokens[last].IsPunctuation(")"))))
                {
                    atStart = !endsItem(analyzer, last);
                }
                else
                {
                    return null;
                }

                while (!isAnchor(analyzer, index))
                {
                    var token = tokens[index];

                    if (skipsGroups && token.IsPunctuation(")"))
                    {
                        index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index);
                    }
                    else if (!(isPart(analyzer, index) || (separatedByCommas && token.IsPunctuation(","))))
                    {
                        return null;
                    }

                    if (--index < 0)
                    {
                        return null;
                    }
                }
            }

            if (header(analyzer, index) is not { } slots)
            {
                return null;
            }

            return atStart ? slots.Start : slots.End;
        }
    }
}
