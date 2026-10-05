using System;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

public sealed partial class SqlKeywordPositionAnalyzer
{
    /// <summary>
    /// 清單片語宣告的選項清單（<c>CREATE LOGIN l WITH PASSWORD = 'x', CHECK_POLICY = OFF</c>、
    /// <c>EXEC p WITH RECOMPILE, RESULT SETS (…)</c>、<c>BACKUP DATABASE d TO DISK = 'x' WITH INIT</c>）：
    /// 錨點是某個清單片語的標頭，選項由那個片語給。
    /// </summary>
    /// <remarks>
    /// 哪些敘述有這種清單只由片語說一次，這裡不列敘述；位置也只有一個，是哪一句由片語的標頭分。
    /// 選項裡寫得出的東西：開始另一句的字、分號與沒關上的左括號之外都是，一整組括號跳過。
    /// 選項寫完之後接的是逗號或下一句，不歸清單管。
    ///
    /// 排在 <see cref="OptionLists"/> 之前：靜態欄位依宣告順序初始化，那份陣列要放它。
    /// </remarks>
    private static readonly OptionList PhraseList = new(
        isAnchor: (analyzer, index) => SqlClausePhraseCatalog.OpensList(analyzer.tokens, index, analyzer),
        isPart: (analyzer, index) => !analyzer.StartsClauseOfItsOwn(index),
        endsItem: null,
        header: (_, _) => new OptionSlots(SqlKeywordPosition.OptionItem, null),
        skipsGroups: true);

    /// <summary>
    /// 敘述自己的選項清單；每一種一筆，形狀相同：往回走過清單、找到錨點、驗證錨點前的標頭。
    /// </summary>
    /// <remarks>
    /// 這幾格接的多半是非關鍵字的選項（<c>LOCAL</c>、<c>ENCRYPTION</c>、<c>COMPRESSION</c>），
    /// 由以位置為鍵的子句片語給；位置判不出來的話片語無從比對，整份目錄全部進場。
    /// 新增一種敘述的選項清單只要加一筆；新的位置照樣要有產生器的樣板與片語。
    /// 標頭之後是逗號清單、選項只看標頭的敘述不必加在這裡：寫成清單片語，走 <see cref="PhraseList"/>；
    /// 標頭中段可變（<c>EXEC p @a = 1 WITH</c>）的片語用 <c>...</c>。
    /// </remarks>
    private static readonly OptionList[] OptionLists =
    {
        // DECLARE c [SCROLL] CURSOR、SET @c = CURSOR [LOCAL FAST_FORWARD …]：選項是一串非關鍵字的識別字，不以逗號分隔。
        new(
            isAnchor: (analyzer, index) => analyzer.IsBareKeyword(index) && analyzer.tokens[index].IsKeyword("CURSOR"),
            isPart: (analyzer, index) => analyzer.IsPlainWord(index),
            endsItem: (_, _) => true,
            header: (analyzer, cursor) => SqlCursorDeclaration.TakesOptions(analyzer.tokens, cursor)
                ? new OptionSlots(SqlKeywordPosition.CursorOption, SqlKeywordPosition.CursorOption)
                : null,
            separatedByCommas: false),

        // CREATE|ALTER SEQUENCE s [AS int] START WITH 1 INCREMENT BY -1 NO CYCLE：與游標選項同一種格子，
        // 只是一項可以帶值、負號與型別的括號。START WITH、RESTART WITH 的 WITH 不是 CTE 的開頭。
        new(
            isAnchor: (analyzer, index) => analyzer.NamesSequence(index),
            isPart: (analyzer, index) => !analyzer.StartsClauseOfItsOwn(index) ||
                (index >= 1 && analyzer.tokens[index].IsKeyword("WITH") &&
                 (analyzer.tokens[index - 1].IsKeyword("START") || analyzer.tokens[index - 1].IsKeyword("RESTART"))),
            endsItem: (_, _) => true,
            header: (_, _) => new OptionSlots(SqlKeywordPosition.SequenceOption, SqlKeywordPosition.SequenceOption),
            separatedByCommas: false,
            skipsGroups: true),

        // 觸發程序標頭之後的 AFTER|FOR|INSTEAD OF INSERT, UPDATE：DDL 事件（CREATE_TABLE、LOGON）是一般識別字。
        // 排在標頭的 WITH 清單前面：AFTER 是非保留字，WITH 清單會把它當成選項名稱。
        // DDL 觸發程序（ON DATABASE、ON ALL SERVER）的事件依標頭而不同，一項的開頭交給清單片語（OptionItem）。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("AFTER") ||
                analyzer.tokens[index].IsKeyword("FOR") ||
                (analyzer.tokens[index].IsKeyword("OF") && index >= 1 && analyzer.tokens[index - 1].IsKeyword("INSTEAD")),
            isPart: (analyzer, index) => analyzer.IsPlainWord(index) || analyzer.IsDmlEvent(index),
            endsItem: (_, _) => true,
            header: (analyzer, anchor) => analyzer.FindTriggerEventSlots(anchor)),

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
        // 一項的開頭（GRANT 、REVOKE GRANT OPTION FOR 與逗號之後）是清單片語的格子，權限名稱由片語給；
        // 判不出位置的話 GRANT CREATE 的 CREATE 會比對成 CREATE 語句的片語。WITH GRANT OPTION 的 GRANT 不是開頭。
        // 資料庫稽核規格括號裡的動作（ADD (SELECT, INSERT ON t BY u)）是同一種寫法，ON 之後同樣是類別或目標；
        // 那裡的動作沒有清單片語，開頭不回位置。
        new(
            isAnchor: (analyzer, index) => analyzer.OpensPermissionList(index),
            isPart: (analyzer, index) => analyzer.IsPermissionPart(index),
            endsItem: (_, _) => true,
            header: (analyzer, anchor) => new OptionSlots(
                analyzer.tokens[anchor].IsPunctuation("(") ? null : SqlKeywordPosition.OptionItem,
                SqlKeywordPosition.PermissionList),
            skipsGroups: true),

        // GRANT … ON [SCHEMA::]dbo.Loan：ON 之後是類別或目標，目標寫完之後是 TO、FROM。
        // ALTER AUTHORIZATION ON 的類別與目標寫法相同。類別可以是幾個字（SEARCH PROPERTY LIST::），第一個字可以是保留字。
        // 資料行層級的權限把資料行清單寫在目標之後（ON Loan (LoanNo)），整組括號也是目標的一部分。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("ON"),
            isPart: (analyzer, index) => analyzer.IsPlainWord(index) ||
                analyzer.tokens[index].IsPunctuation(".") || analyzer.tokens[index].IsPunctuation("::") ||
                analyzer.NamesClass(index),
            endsItem: (analyzer, index) => analyzer.IsPlainWord(index) || analyzer.tokens[index].IsPunctuation(")"),
            header: (analyzer, on) => on >= 1 && (analyzer.FindStatementSlot(on - 1) == SqlKeywordPosition.PermissionList ||
                    (on >= 2 && analyzer.tokens[on - 1].IsKeyword("AUTHORIZATION") && analyzer.tokens[on - 2].IsKeyword("ALTER")))
                ? new OptionSlots(SqlKeywordPosition.PermissionOn, SqlKeywordPosition.PermissionTarget)
                : null,
            separatedByCommas: false,
            skipsGroups: true),

        // CREATE|ALTER PROCEDURE|FUNCTION|VIEW … WITH：EXECUTE AS、INLINE = ON、RETURNS NULL ON NULL INPUT。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsKeyword("WITH"),
            isPart: (analyzer, index) => analyzer.IsModuleOptionPart(index),
            endsItem: (analyzer, index) => analyzer.EndsModuleOption(index),
            header: (analyzer, with) => analyzer.FindModuleOptionHeader(with)),

        // 外部索引鍵 REFERENCES dbo.Copy (CopyNo) ON DELETE CASCADE：參考與每個動作寫完之後是 ON、NOT 與其他條件約束。
        // 動作寫到一半（ON DELETE SET ）由片語給。GRANT REFERENCES 的 REFERENCES 是權限。
        new(
            isAnchor: (analyzer, index) => analyzer.IsBareKeyword(index) && analyzer.tokens[index].IsKeyword("REFERENCES"),
            isPart: (analyzer, index) => analyzer.IsReferencesPart(index),
            endsItem: (analyzer, index) => analyzer.EndsReferencesItem(index),
            header: (analyzer, references) => analyzer.NamesPermission(references)
                ? null
                : new OptionSlots(null, SqlKeywordPosition.ReferencesTail),
            separatedByCommas: false,
            skipsGroups: true),

        // CREATE INDEX … WITH (ONLINE = ON, FILLFACTOR = 80)、資料表定義裡的 INDEX ix (a) WITH (…)：左括號與逗號之後是下一個選項。
        new(
            isAnchor: (analyzer, index) => analyzer.tokens[index].IsPunctuation("(") &&
                index >= 1 && analyzer.tokens[index - 1].IsKeyword("WITH"),
            isPart: (analyzer, index) => !analyzer.StartsClauseOfItsOwn(index),
            endsItem: null,
            header: (analyzer, open) => analyzer.DefinesIndex(open - 1)
                ? new OptionSlots(SqlKeywordPosition.IndexOption, null)
                : null,
            skipsGroups: true),

        PhraseList
    };

    /// <summary>
    /// <paramref name="last"/> 之後是清單片語的一項開頭時，那份清單的錨點；不是就回 -1。
    /// </summary>
    /// <remarks>
    /// 子句片語拿它比對是哪一句的清單，走訪與 <see cref="SqlKeywordPosition.OptionItem"/> 是同一條規則；
    /// 目錄物件那一格拿它認 <c>DEFAULT_SCHEMA = </c> 是不是清單的一項。
    /// </remarks>
    internal int FindPhraseListAnchor(int last)
    {
        return PhraseList.FindAnchor(this, last, out _);
    }

    /// <summary>
    /// <paramref name="last"/> 之後是某一種敘述自己的格子時，那個位置；不是就回 null。
    /// </summary>
    /// <remarks>
    /// 每一種都只認「往回緊鄰的形狀」或所屬的動詞，走不出這一句。
    /// </remarks>
    private SqlKeywordPosition? FindStatementSlot(int last)
    {
        if (IntroducesGrantee(last))
        {
            return SqlKeywordPosition.PermissionGrantee;
        }

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

        if (EndsOffsetValue(last))
        {
            return SqlKeywordPosition.OffsetTail;
        }

        if (EndsFunctionParameters(last))
        {
            return SqlKeywordPosition.FunctionReturns;
        }

        if (EndsTableSampleSize(last))
        {
            return SqlKeywordPosition.TableSampleTail;
        }

        if (EndsPivotPart(last))
        {
            return SqlKeywordPosition.PivotClause;
        }

        if (FindResultSetSlot(last) is { } resultSet)
        {
            return resultSet;
        }

        foreach (var list in OptionLists)
        {
            if (list.Resolve(this, last) is { } position)
            {
                return position;
            }
        }

        // 排在選項清單之後：外部索引鍵寫完的那一格（ReferencesTail）多接 ON DELETE。
        return EndsColumnDefinitionPart(last) ? SqlKeywordPosition.ColumnDefinitionTail : null;
    }

    /// <summary>
    /// <paramref name="last"/> 寫完一項資料行定義的型別（或計算資料行的運算式），或之後的一個選項。
    /// </summary>
    /// <remarks>
    /// 一項從哪裡開始見 <see cref="FindColumnDefinitionStart"/>；開頭是新資料行的名稱，名稱之後是型別或 <c>AS</c> 運算式。
    /// 之後的選項不逐一認：寫完一個運算元（名稱、常值、右括號、<c>NULL</c>）就是寫完一個選項。
    /// 寫到一半的（<c>NOT</c>、<c>MASKED</c>、<c>CONSTRAINT c</c>）由以這一格為前一格的片語接手；
    /// 停在其餘關鍵字上的（<c>DEFAULT</c>、<c>COLLATE</c>、<c>IDENTITY</c>）照舊判不出位置——<c>DEFAULT </c>之後要的是運算式。
    /// </remarks>
    private bool EndsColumnDefinitionPart(int last)
    {
        if (!SqlOperand.Ends(tokens, last) && !IsBareKeyword(last))
        {
            return false;
        }

        var start = FindColumnDefinitionStart(last);

        if (start < 0 || start >= last)
        {
            return false;
        }

        var head = SkipColumnHead(start, last);

        return head == last + 1 || (head > start && head <= last && SqlOperand.Ends(tokens, last));
    }

    /// <summary>
    /// <paramref name="last"/> 所在那一項資料行定義的第一個詞元；不在資料行定義裡回 -1。
    /// </summary>
    /// <remarks>
    /// 定義清單的左括號與那一層的逗號（<c>CREATE TABLE t (</c>、<c>DECLARE @t TABLE (</c>、<c>OPENJSON(@j) WITH (</c>，
    /// 見 <see cref="OpensColumnDefinitions"/>）、<c>ALTER TABLE t ADD</c> 與新增清單的逗號、<c>ALTER TABLE t ALTER COLUMN</c>。
    /// 一組括號整組跳過；走到別的括號、分號或動詞（<see cref="FindVerb"/>）就不在這裡。
    /// </remarks>
    private int FindColumnDefinitionStart(int last)
    {
        for (var index = last; index >= 0; index--)
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

            if (token.IsPunctuation("("))
            {
                return OpensColumnDefinitions(index) ? index + 1 : -1;
            }

            if (token.IsPunctuation(","))
            {
                return OpensColumnDefinitions(FindUnclosedParenthesis(index - 1)) || ContinuesAlterTableAdd(index)
                    ? index + 1
                    : -1;
            }

            if (token.IsPunctuation(";"))
            {
                return -1;
            }

            if (!IsBareKeyword(index))
            {
                continue;
            }

            if (token.IsKeyword("ADD"))
            {
                return IsAlterTableTarget(index - 1) ? index + 1 : -1;
            }

            if (token.IsKeyword("COLUMN") && index >= 1 && tokens[index - 1].IsKeyword("ALTER"))
            {
                return IsAlterTableTarget(index - 2) ? index + 1 : -1;
            }

            // WITH 帶不出子句（MASKED WITH (…)、UNIQUE WITH FILLFACTOR = 80），與 FindVerb 同一條。
            if (IsVerbCandidate(index) && !token.IsKeyword("WITH"))
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// 從 <paramref name="start"/> 的新資料行名稱跳過型別或計算資料行的 <c>AS</c> 運算式，回傳之後的位置；
    /// 不是這種開頭（<c>CONSTRAINT</c>、<c>INDEX</c>、<c>PERIOD FOR</c>）或還沒寫完時回 -1。
    /// </summary>
    private int SkipColumnHead(int start, int last)
    {
        var name = tokens[start];

        if (name.Kind != SqlTokenKind.Identifier || IsBareKeyword(start))
        {
            return -1;
        }

        // 計算資料行：運算式寫到哪裡為止，取 AS 之後整段是一個運算式的最後一個詞元。
        if (tokens[start + 1].IsKeyword("AS") && IsBareKeyword(start + 1))
        {
            for (var end = last; end > start + 1; end--)
            {
                if (SqlOperand.SkipBackward(tokens, end) == start + 2)
                {
                    return end + 1;
                }
            }

            return -1;
        }

        // 型別不是關鍵字；是關鍵字的只有多字型別的第一個字（NATIONAL）。
        var type = SqlTokenNavigator.SkipDataType(tokens, start + 1, last + 1);

        return type > start + 1 &&
            (!IsBareKeyword(start + 1) || SqlDataTypeCatalog.CountWords(tokens, start + 1, last + 1) > 0)
                ? type
                : -1;
    }

    /// <summary><paramref name="last"/> 寫完 ORDER BY 的 <c>OFFSET</c> 值，一個運算式（<see cref="SqlOperand.SkipBackward"/>）。</summary>
    private bool EndsOffsetValue(int last)
    {
        var offset = SqlOperand.SkipBackward(tokens, last) - 1;

        return offset >= 1 &&
            IsBareKeyword(offset) &&
            tokens[offset].IsKeyword("OFFSET") &&
            (FindClausePosition(offset - 1) & SqlKeywordPosition.OrderByTail) != SqlKeywordPosition.None;
    }

    /// <summary><paramref name="last"/> 寫完 <c>TABLESAMPLE [SYSTEM] (</c> 的樣本大小：數值或變數。</summary>
    private bool EndsTableSampleSize(int last)
    {
        if (last < 2 ||
            tokens[last].Kind is not (SqlTokenKind.Number or SqlTokenKind.Variable) ||
            !tokens[last - 1].IsPunctuation("("))
        {
            return false;
        }

        var sample = tokens[last - 2].IsKeyword("SYSTEM") ? last - 3 : last - 2;
        return sample >= 0 && tokens[sample].IsKeyword("TABLESAMPLE");
    }

    /// <summary>
    /// <paramref name="last"/> 寫完 PIVOT、UNPIVOT 括號裡的一段：第一段（<c>PIVOT (SUM(x) </c>、
    /// <c>UNPIVOT (v </c>，之後是 FOR），或 FOR 的資料行（<c>FOR y </c>，之後是 IN）。
    /// </summary>
    private bool EndsPivotPart(int last)
    {
        var open = FindUnclosedParenthesis(last);

        if (open < 1 || !(tokens[open - 1].IsKeyword("PIVOT") || tokens[open - 1].IsKeyword("UNPIVOT")))
        {
            return false;
        }

        if (last - 1 > open && tokens[last - 1].IsKeyword("FOR"))
        {
            return IsPlainWord(last);
        }

        var first = tokens[last].IsPunctuation(")") ? SqlTokenNavigator.FindOpeningParenthesis(tokens, last) - 1 : last;
        return first == open + 1 && tokens[first].Kind == SqlTokenKind.Identifier;
    }

    /// <summary>
    /// <paramref name="last"/> 之後在 <c>EXEC … WITH RESULT SETS (…)</c> 裡的位置；不在那裡回 null。
    /// </summary>
    /// <remarks>
    /// 兩層括號。外層是結果集清單：左括號與逗號之後是下一個結果集。內層是一組資料行定義：
    /// 左括號與逗號之後是新資料行名稱，名稱之後的型別由型別位置問這裡，型別（與定序）寫完之後
    /// 是 COLLATE、NULL、NOT NULL。停在 <c>NULL</c> 之後的那一項已經寫完，不回位置。
    /// </remarks>
    private SqlKeywordPosition? FindResultSetSlot(int last)
    {
        var token = tokens[last];

        if (token.IsPunctuation("(") || token.IsPunctuation(","))
        {
            var open = FindUnclosedParenthesis(last);

            return OpensResultSets(open) ? SqlKeywordPosition.ResultSetList
                : OpensResultSetColumns(open) ? SqlKeywordPosition.ResultSetColumn
                : null;
        }

        var columns = FindUnclosedParenthesis(last);

        if (!OpensResultSetColumns(columns))
        {
            return null;
        }

        // 游標所在的那一項從最後一個同層逗號之後開始：名稱、型別，之後可以有 COLLATE 定序。
        var start = columns + 1;

        for (var comma = SqlTokenNavigator.FindListItemEnd(tokens, start, last); comma < last;
             comma = SqlTokenNavigator.FindListItemEnd(tokens, start, last))
        {
            start = comma + 1;
        }

        if (start >= last || tokens[start].Kind != SqlTokenKind.Identifier)
        {
            return null;
        }

        var end = SqlTokenNavigator.SkipDataType(tokens, start + 1, last + 1);

        if (end == start + 1)
        {
            return null;
        }

        if (end + 1 <= last && tokens[end].IsKeyword("COLLATE") && tokens[end + 1].Kind == SqlTokenKind.Identifier)
        {
            end += 2;
        }

        return end == last + 1 ? SqlKeywordPosition.ResultSetColumnTail : null;
    }

    /// <summary><paramref name="open"/> 是 <c>EXEC … WITH RESULT SETS (</c> 的左括號。</summary>
    /// <remarks>
    /// RESULT SETS 是 EXEC 選項清單裡的一項：前面是那份清單的一項開頭，而清單屬於 EXEC。
    /// 別的選項清單寫不出 RESULT SETS，剖析器也不收。
    /// </remarks>
    private bool OpensResultSets(int open)
    {
        return open >= 3 &&
            tokens[open].IsPunctuation("(") &&
            tokens[open - 1].IsKeyword("SETS") &&
            tokens[open - 2].IsKeyword("RESULT") &&
            FindStatementSlot(open - 3) == SqlKeywordPosition.OptionItem &&
            FindVerb(open - 3) is var verb and >= 0 &&
            (tokens[verb].IsKeyword("EXEC") || tokens[verb].IsKeyword("EXECUTE"));
    }

    /// <summary><paramref name="open"/> 開啟結果集清單裡的一組資料行定義。</summary>
    private bool OpensResultSetColumns(int open)
    {
        return open >= 1 &&
            tokens[open].IsPunctuation("(") &&
            (tokens[open - 1].IsPunctuation("(") || tokens[open - 1].IsPunctuation(",")) &&
            OpensResultSets(FindUnclosedParenthesis(open - 1));
    }

    /// <summary>
    /// <paramref name="with"/> 的 WITH 屬於一個索引的定義：<c>CREATE [UNIQUE] [CLUSTERED] INDEX</c>（含 XML、JSON 索引）這一句，
    /// 或資料表定義裡的內嵌索引（<c>INDEX ix NONCLUSTERED (a)</c>，<c>ALTER TABLE t ADD INDEX</c> 也是）。
    /// </summary>
    /// <remarks>
    /// 問的是這一句或這一項的開頭而不是緊鄰的形狀：索引鍵、INCLUDE 與篩選的 WHERE 都可能夾在中間。
    /// 內嵌索引與 CREATE INDEX 收同一份選項；條件約束的 <c>PRIMARY KEY (a) WITH (</c> 不在這裡，由片語給。
    /// </remarks>
    private bool DefinesIndex(int with)
    {
        var start = FindStatementStart(with - 1);

        if (start < with &&
            FindCreatedKind(start, endsAt: false) is { } kind &&
            start + kind.Words.Length < with &&
            string.Equals(kind.Words[kind.Words.Length - 1], "INDEX", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var item = FindColumnDefinitionStart(with - 1);

        for (var index = item; index >= 0 && index < with; index++)
        {
            if (IsBareKeyword(index) && tokens[index].IsKeyword("INDEX"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="last"/> 關上 <c>CREATE|ALTER FUNCTION</c> 或 <c>CREATE AGGREGATE</c> 名稱之後的參數清單。
    /// </summary>
    /// <remarks>
    /// 名稱可以限定（<c>dbo.fn_Fee</c>）；<c>CREATE OR ALTER</c> 的 FUNCTION 前面同樣是 ALTER。
    /// 彙總的參數清單之後同樣接 <c>RETURNS</c>。
    /// </remarks>
    private bool EndsFunctionParameters(int last)
    {
        if (!tokens[last].IsPunctuation(")"))
        {
            return false;
        }

        var name = SqlTokenNavigator.FindOpeningParenthesis(tokens, last) - 1;

        if (name < 2 || !IsPlainWord(name))
        {
            return false;
        }

        while (name >= 3 && tokens[name - 1].IsPunctuation(".") && IsPlainWord(name - 2))
        {
            name -= 2;
        }

        var function = name - 1;

        // AGGREGATE 不在關鍵字目錄裡，只認沒加引號的字。
        return (tokens[function].IsKeyword("FUNCTION") || tokens[function].IsKeyword("AGGREGATE")) &&
            (tokens[function - 1].IsKeyword("CREATE") || tokens[function - 1].IsKeyword("ALTER"));
    }

    /// <summary>
    /// <paramref name="index"/> 的字是 GRANT／DENY／REVOKE 權限清單一項的開頭（REFERENCES 不是外部索引鍵、CREATE 不是建立敘述）：
    /// 緊接清單的開頭（含 <c>REVOKE GRANT OPTION FOR</c>）或逗號。
    /// </summary>
    private bool NamesPermission(int index) =>
        index >= 1 && (tokens[index - 1].IsPunctuation(",") || OpensPermissionList(index - 1));

    /// <summary>
    /// 外部索引鍵的 <c>REFERENCES</c> 之後寫得出這個詞元：參考的名稱、點號，以及
    /// <c>ON DELETE|UPDATE CASCADE|NO ACTION|SET NULL|SET DEFAULT</c> 與 <c>NOT FOR REPLICATION</c> 裡的字。
    /// </summary>
    private bool IsReferencesPart(int index)
    {
        var token = tokens[index];

        return IsPlainWord(index) || token.IsPunctuation(".") ||
            token.IsKeyword("ON") || token.IsKeyword("DELETE") || token.IsKeyword("UPDATE") ||
            token.IsKeyword("CASCADE") || token.IsKeyword("NO") || token.IsKeyword("ACTION") ||
            token.IsKeyword("SET") || token.IsKeyword("NULL") || token.IsKeyword("DEFAULT") ||
            token.IsKeyword("NOT") || token.IsKeyword("FOR") || token.IsKeyword("REPLICATION");
    }

    /// <summary>外部索引鍵寫到這個詞元已經完整：參考的名稱或資料行清單、一個參考動作、NOT FOR REPLICATION。</summary>
    private bool EndsReferencesItem(int index)
    {
        var token = tokens[index];

        return IsPlainWord(index) || token.IsPunctuation(")") ||
            token.IsKeyword("CASCADE") || token.IsKeyword("ACTION") || token.IsKeyword("REPLICATION") ||
            ((token.IsKeyword("NULL") || token.IsKeyword("DEFAULT")) && index >= 1 && tokens[index - 1].IsKeyword("SET"));
    }

    /// <summary><paramref name="last"/> 寫完觸發程序的標頭：目標，或目標之後的 WITH 選項。</summary>
    private bool EndsTriggerHeader(int last) =>
        last >= 0 && FindStatementSlot(last) == SqlKeywordPosition.TriggerHeader;

    /// <summary><paramref name="anchor"/> 的 AFTER、FOR、INSTEAD OF 接在觸發程序的標頭之後時，事件清單的兩種位置。</summary>
    private OptionSlots? FindTriggerEventSlots(int anchor)
    {
        var header = tokens[anchor].IsKeyword("OF") ? anchor - 2 : anchor - 1;

        if (!EndsTriggerHeader(header))
        {
            return null;
        }

        return new OptionSlots(
            FiresOnDdlEvents(header) ? SqlKeywordPosition.OptionItem : SqlKeywordPosition.TriggerEvent,
            SqlKeywordPosition.TriggerEventEnd);
    }

    /// <summary><paramref name="headerEnd"/> 所在的觸發程序寫在 <c>ON DATABASE</c> 或 <c>ON ALL SERVER</c> 上，事件是 DDL 與登入事件。</summary>
    private bool FiresOnDdlEvents(int headerEnd)
    {
        for (var index = FindStatementStart(headerEnd); index < headerEnd; index++)
        {
            if (tokens[index].IsKeyword("ON"))
            {
                return tokens[index + 1].IsKeyword("DATABASE") || tokens[index + 1].IsKeyword("ALL");
            }
        }

        return false;
    }

    /// <summary><paramref name="index"/> 是 <c>CREATE|ALTER SEQUENCE</c> 之後序列名稱的最後一個詞元。</summary>
    private bool NamesSequence(int index)
    {
        if (!IsPlainWord(index))
        {
            return false;
        }

        var name = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, index);

        return name >= 2 &&
            tokens[name - 1].IsKeyword("SEQUENCE") &&
            (tokens[name - 2].IsKeyword("CREATE") || tokens[name - 2].IsKeyword("ALTER"));
    }

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
            InMerge(last);
    }

    /// <summary><paramref name="index"/> 所在的那一句以 MERGE 開頭。</summary>
    /// <remarks>
    /// MERGE 可以寫在 FROM 的括號裡、以 OUTPUT 交出資料列（<c>INSERT … SELECT … FROM (MERGE … OUTPUT …) AS c</c>）：
    /// 那一句從還開著的左括號之後算起。只問整句的開頭的話，括號裡的 WHEN 屬於外層的 INSERT，動作寫完列不出下一個 WHEN。
    /// </remarks>
    private bool InMerge(int index)
    {
        var start = FindStatementStart(index);
        var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, index);

        return tokens[open >= start ? open + 1 : start].IsKeyword("MERGE");
    }

    /// <summary>
    /// <paramref name="last"/> 之後是主體：權限或目標寫完之後的 TO、FROM、BY（稽核動作），或主體清單 <c>TO a, </c> 的逗號。
    /// </summary>
    /// <remarks>
    /// 認的是前一格的位置，不是字：<c>SELECT … FROM</c>、<c>BACKUP … TO</c> 的前一格不是權限。
    /// ALTER AUTHORIZATION 的 ON 目標與權限同一個位置，它的 TO 也在這裡。
    /// </remarks>
    private bool IntroducesGrantee(int last)
    {
        var index = last;

        while (index >= 2 && tokens[index].IsPunctuation(",") && tokens[index - 1].Kind == SqlTokenKind.Identifier)
        {
            index -= 2;
        }

        var token = tokens[index];

        if (index < 1 || !(token.IsKeyword("TO") || token.IsKeyword("FROM") || token.IsKeyword("BY")))
        {
            return false;
        }

        // 判不出前一格（Any）也帶著這兩個位元，那不算：SELECT * FROM 的前一格判不出來時不是權限。
        var before = PositionBefore(index);
        return before != SqlKeywordPosition.Any &&
            (before & (SqlKeywordPosition.PermissionList | SqlKeywordPosition.PermissionTarget)) != SqlKeywordPosition.None;
    }

    /// <summary>
    /// GRANT、DENY、REVOKE 與 <c>REVOKE GRANT OPTION FOR</c> 開始權限清單；<c>WITH GRANT OPTION</c> 的 GRANT 不是。
    /// </summary>
    private bool OpensPermissionList(int index) =>
        (IsBareKeyword(index) &&
         (tokens[index].IsKeyword("GRANT") || tokens[index].IsKeyword("DENY") || tokens[index].IsKeyword("REVOKE")) &&
         !(index >= 1 && tokens[index - 1].IsKeyword("WITH"))) ||
        (index >= 3 && tokens[index].IsKeyword("FOR") && tokens[index - 1].IsKeyword("OPTION") &&
         tokens[index - 2].IsKeyword("GRANT") && tokens[index - 3].IsKeyword("REVOKE")) ||
        OpensAuditActions(index);

    /// <summary>
    /// <paramref name="index"/> 是資料庫稽核規格 <c>ADD (</c>、<c>DROP (</c> 的左括號：裡面是動作群組，
    /// 或 <c>SELECT, INSERT ON t BY u</c> 這種動作。
    /// </summary>
    /// <remarks>伺服器稽核規格的括號裡只有動作群組，寫完就關上括號，不是權限的寫法。</remarks>
    private bool OpensAuditActions(int index)
    {
        if (index < 1 || !tokens[index].IsPunctuation("(") ||
            !(tokens[index - 1].IsKeyword("ADD") || tokens[index - 1].IsKeyword("DROP")))
        {
            return false;
        }

        // ADD、DROP 能開始一句，語句開頭會停在它們上；稽核規格的標頭在逗號隔開的上一組之前。
        for (var verb = index - 2; verb >= 2; verb--)
        {
            if (tokens[verb].IsKeyword("SPECIFICATION"))
            {
                return tokens[verb - 1].IsKeyword("AUDIT") && tokens[verb - 2].IsKeyword("DATABASE");
            }

            if (tokens[verb].IsPunctuation(";") || tokens[verb].IsKeyword("GO"))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="index"/> 是 <c>::</c> 之前的類別裡的一個字：<c>OBJECT::</c>、<c>SEARCH PROPERTY LIST::</c>、
    /// <c>EXTERNAL MODEL::</c>（第一個字是保留字）。
    /// </summary>
    private bool NamesClass(int index)
    {
        for (var next = index; next < tokens.Count; next++)
        {
            if (tokens[next].IsPunctuation("::"))
            {
                return next > index;
            }

            if (tokens[next].Kind != SqlTokenKind.Identifier || tokens[next].IsQuoted)
            {
                return false;
            }
        }

        return false;
    }

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
        if (!SqlOperand.Ends(tokens, last) &&
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
                if (FindUnclosedCase(index - 1) >= 0 || !InMerge(index))
                {
                    return null;
                }

                return token.IsKeyword("WHEN")
                    ? (hasAnd && SqlOperand.Ends(tokens, last) ? SqlKeywordPosition.CaseArm : null)
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
            return then + 2 < last && tokens[then + 2].IsKeyword("SET") && SqlOperand.Ends(tokens, last)
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

    /// <summary><paramref name="on"/> 的 ON 前面是 MERGE 的 <c>USING 來源 [AS] [別名 [(資料行清單)]]</c>。</summary>
    /// <remarks>
    /// 資料行清單只接在別名後面（<c>USING (SELECT @a, @b) AS s (Code, Title) ON</c>）；不跳過它的話右括號被當成來源本身，
    /// 往回走到 AS 就停，ON 之後的述詞寫完列不出 <c>WHEN</c>。
    /// </remarks>
    private bool FollowsMergeSource(int on)
    {
        var index = on - 1;

        if (index >= 0 && tokens[index].IsPunctuation(")") &&
            SqlTokenNavigator.FindOpeningParenthesis(tokens, index) is var columns && NamesMergeSourceAlias(columns - 1))
        {
            index = columns - 1;
        }

        if (NamesMergeSourceAlias(index))
        {
            index--;
        }

        if (index >= 0 && tokens[index].IsKeyword("AS"))
        {
            index--;
        }

        // 衍生資料表的括號，或函式的引數清單（USING dbo.fn_Copies(1) ON）之後是函式名稱。
        if (index >= 0 && tokens[index].IsPunctuation(")"))
        {
            index = SqlTokenNavigator.FindOpeningParenthesis(tokens, index) - 1;
        }

        if (index >= 0 && tokens[index].Kind == SqlTokenKind.Identifier && !tokens[index].IsKeyword("USING"))
        {
            index = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, index) - 1;
        }

        return index >= 0 && tokens[index].IsKeyword("USING");
    }

    /// <summary><paramref name="index"/> 是 MERGE 來源的別名：不是來源的名稱本身，也不是限定名稱的一段。</summary>
    private bool NamesMergeSourceAlias(int index) =>
        index >= 1 && tokens[index].Kind == SqlTokenKind.Identifier && !IsBareKeyword(index) &&
        !tokens[index - 1].IsPunctuation(".") && !tokens[index - 1].IsKeyword("USING");

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

        var open = tokens[last - 1].IsPunctuation("(") ? last - 1 : FindUnclosedParenthesis(last - 1);

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

        return new OptionSlots(start, SqlKeywordPosition.ModuleHeader);
    }

    /// <summary>
    /// 這個詞元開始另一個子句或另一句：能開始一句的關鍵字、分號、沒關上的左括號。
    /// </summary>
    /// <remarks>清單項寫不出這種詞元；子句片語的中段 <c>,*</c> 走過清單項也照這一條，見 <see cref="SqlClausePhrase"/>。</remarks>
    internal bool StartsClauseOfItsOwn(int index)
    {
        var token = tokens[index];

        return token.IsPunctuation(";") || token.IsPunctuation("(") ||
            (token.Kind == SqlTokenKind.Identifier && IsBareKeyword(index) && StartsStatement(token) && !NamesPermission(index));
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
            var anchor = FindAnchor(analyzer, last, out var atStart);

            if (anchor < 0 || header(analyzer, anchor) is not { } slots)
            {
                return null;
            }

            return atStart ? slots.Start : slots.End;
        }

        /// <summary>
        /// 從 <paramref name="last"/> 往回走過清單，回傳錨點；<paramref name="last"/> 不在這份清單裡時回 -1。
        /// </summary>
        /// <param name="atStart"><paramref name="last"/> 之後是一項的開頭，而不是寫完一項之後。</param>
        public int FindAnchor(SqlKeywordPositionAnalyzer analyzer, int last, out bool atStart)
        {
            var tokens = analyzer.tokens;
            var index = last;
            atStart = true;

            if (isAnchor(analyzer, last))
            {
                return last;
            }

            if (tokens[last].IsPunctuation(","))
            {
                if (!separatedByCommas)
                {
                    return -1;
                }
            }
            else if (endsItem is not null && (isPart(analyzer, last) || (skipsGroups && tokens[last].IsPunctuation(")"))))
            {
                atStart = !endsItem(analyzer, last);
            }
            else
            {
                return -1;
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
                    return -1;
                }

                if (--index < 0)
                {
                    return -1;
                }
            }

            return index;
        }
    }
}
