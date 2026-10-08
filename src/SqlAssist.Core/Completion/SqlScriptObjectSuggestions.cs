using System;
using System.Collections.Generic;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 指令碼自己宣告的物件：資料來源（CTE、暫存資料表、資料表變數、xml 變數）、暫存程序與游標。
/// </summary>
/// <remarks>
/// 建議清單的資料庫物件全部來自中繼資料，而中繼資料只看得到目前連線資料庫的
/// <c>sys.objects</c>。CTE 與資料表變數只存在於這份指令碼裡，暫存資料表與暫存程序在
/// tempdb 裡，全都不在那份清單上——症狀是使用者上一行才寫下的名稱，下一行打
/// <c>FROM </c> 或 <c>EXEC </c> 卻一個建議都沒有，而那正是他最需要補字的時候
/// （名稱是他剛取的，還沒背起來）。
///
/// 兩份清單放在一起，因為井號名稱要在兩者之間分一次：分辨規則只有
/// <see cref="CollectTemporaryProcedures"/> 一份，兩邊各寫一條的話，同一個名稱會
/// 同時出現在 <c>FROM</c> 與 <c>EXEC</c> 之後，或兩邊都沒有。
///
/// 只在游標真的落在對應位置時才建立。掃描本身是單趟線性的，但這條路徑
/// 在每一次按鍵上，而絕大多數位置根本用不到這一份。
/// </remarks>
public static class SqlScriptObjectSuggestions
{
    /// <summary>
    /// 組出這份指令碼宣告的資料來源。
    /// </summary>
    /// <param name="tokens">整份指令碼的詞法單元。</param>
    /// <param name="resolver">
    /// 與欄位解析共用同一次掃描的那一份。CTE 名冊與「這個名稱是一張什麼樣的表」
    /// 都問它，呼叫端不必為了拿名稱再掃一次同一份文字。
    ///
    /// 資料行掛在建議項上，提交之後 <c>INSERT INTO #tmp</c> 才展得開整句——查不到
    /// 中繼資料的名稱如果只補一個字，使用者還是得自己把每一個欄位打一遍。
    /// <c>SELECT … INTO</c> 那一種的資料行是延後算的，這裡只會付到名稱的成本。
    /// </param>
    /// <param name="triggerTable">
    /// 游標在 DML 觸發程序裡時的父資料表（<see cref="SqlStatementScope.TriggerTable"/>）：
    /// <c>inserted</c>、<c>deleted</c> 也是只在這份指令碼裡才有的資料來源。
    /// </param>
    public static IReadOnlyList<SqlSuggestion> DataSources(
        IReadOnlyList<SqlToken> tokens,
        SqlColumnSourceResolver resolver,
        SqlTableReference? triggerTable = null)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        List<SqlSuggestion>? suggestions = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (triggerTable?.Path is { } parent)
        {
            foreach (var name in SqlChangeTables.Names)
            {
                seen.Add(name);
                (suggestions ??= new List<SqlSuggestion>()).Add(new SqlSuggestion(
                    name,
                    name,
                    SqlKindText.Alias,
                    ScriptSuggestionText.AliasOf(name, parent.ToString()),
                    SuggestionKind.ScriptDataSource));
            }
        }

        foreach (var name in resolver.CommonTableExpressionNames)
        {
            if (seen.Add(name))
            {
                (suggestions ??= new List<SqlSuggestion>()).Add(
                    DataSource(name, SqlKindText.CommonTableExpression));
            }
        }

        // 暫存資料表不必分辨是哪一句建立的：CREATE TABLE、SELECT INTO、INSERT INTO
        // 各認一次的話，漏掉的那一種寫法就會安靜地少一個名稱。反過來認的是暫存程序——
        // 井號名稱只有這兩種意思，而程序的名稱只寫在少數幾個固定的位置。
        seen.UnionWith(CollectTemporaryProcedures(tokens));

        foreach (var token in tokens)
        {
            if (IsTemporaryName(token) && seen.Add(token.Value))
            {
                (suggestions ??= new List<SqlSuggestion>()).Add(
                    DataSource(token.Value, SqlKindText.TemporaryTable, resolver.FindScriptTable(token.Value)));
            }
        }

        // 資料表變數只認得出名冊裡的那些。井號那一份看形狀就分得完，這一份不行：
        // @rows 與 @readerId 是同一種詞元，分辨的憑據只有 DECLARE @rows TABLE (…)
        // 這份宣告本身。反過來把每個小老鼠詞元都當成資料來源，FROM 之後就會列出
        // 使用者宣告的每一個純量變數。
        foreach (var table in resolver.ScriptTables.Values)
        {
            if (table.Name.Length > 1 && table.Name[0] == '@' && seen.Add(table.Name))
            {
                (suggestions ??= new List<SqlSuggestion>()).Add(
                    DataSource(table.Name, SqlKindText.TableVariable, table));
            }
        }

        // xml 變數在資料來源的位置接 .nodes(…)（FROM @x.nodes('/a') T(c)），一樣只認宣告。只補名字、不帶點號：
        // 點號之後沒有方法清單，提交字元打的點號照樣接上去。
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == SqlTokenKind.Variable &&
                !seen.Contains(tokens[index].Value) &&
                SqlScriptVariableSuggestions.DeclaredType(tokens, index) is { } type &&
                type.Equals("xml", StringComparison.OrdinalIgnoreCase) &&
                seen.Add(tokens[index].Value))
            {
                (suggestions ??= new List<SqlSuggestion>()).Add(DataSource(tokens[index].Value, type.ToUpperInvariant()));
            }
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    /// <summary>
    /// 組出這份指令碼建立或呼叫過的暫存程序（<c>#p</c>、<c>##p</c>）。
    /// </summary>
    /// <remarks>
    /// 種類就是 <see cref="SuggestionKind.Procedure"/>：它接在 <c>EXEC</c> 之後、
    /// 批次第一句也能省略 <c>EXEC</c>，這些規則與資料庫裡的程序完全相同。
    /// 沒有結構描述，所以插入文字不會補上 <c>dbo.</c>；沒有中繼資料可查，
    /// 所以提交時只補名稱，不展開具名參數。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Procedures(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        List<SqlSuggestion>? suggestions = null;

        foreach (var name in CollectTemporaryProcedures(tokens))
        {
            (suggestions ??= new List<SqlSuggestion>()).Add(new SqlSuggestion(
                name,
                name,
                SqlKindText.TemporaryProcedure,
                ScriptSuggestionText.NameWithDescription(name, SqlKindText.TemporaryProcedure),
                SuggestionKind.Procedure));
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    /// <summary>
    /// 組出這份指令碼宣告的具名游標（<c>DECLARE c CURSOR</c>）。
    /// </summary>
    /// <remarks>
    /// 整份指令碼的宣告都算，與暫存資料表相同：只收寫在上面的，換來的只是回頭改上面幾行時少一個名稱。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Cursors(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        List<SqlSuggestion>? suggestions = null;

        foreach (var name in SqlCursorDeclaration.CollectNames(tokens))
        {
            (suggestions ??= new List<SqlSuggestion>()).Add(new SqlSuggestion(
                name,
                name,
                SqlKindText.Cursor,
                ScriptSuggestionText.NameWithDescription(name, SqlKindText.Cursor),
                SuggestionKind.Cursor));
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    /// <summary>
    /// 組出游標所在查詢的 WINDOW 子句取的具名視窗（<c>WINDOW w AS (…)</c> 的 <c>w</c>）。
    /// </summary>
    /// <remarks>
    /// 與游標不同，視窗名稱只在它那個查詢裡有效：範圍取自 <see cref="SqlScopeAnalyzer"/>，WINDOW 子句
    /// 往往寫在游標之後（<c>SELECT SUM(a) OVER | FROM t WINDOW w AS (…)</c>）。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Windows(string sql, IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var scope = SqlScopeAnalyzer.Analyze(sql, tokens, caretPosition);
        var start = 0;

        while (start < tokens.Count && tokens[start].Start < scope.Start)
        {
            start++;
        }

        var end = start;

        while (end < tokens.Count && tokens[end].Start < scope.End)
        {
            end++;
        }

        List<SqlSuggestion>? suggestions = null;

        foreach (var name in SqlWindowClause.CollectNames(tokens, start, end, caretPosition))
        {
            (suggestions ??= new List<SqlSuggestion>()).Add(new SqlSuggestion(
                name,
                name,
                SqlKindText.Window,
                ScriptSuggestionText.NameWithDescription(name, SqlKindText.Window),
                SuggestionKind.Window));
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    /// <summary>
    /// 這份指令碼裡當成程序用的井號名稱，依出現順序、不重複。
    /// </summary>
    /// <remarks>
    /// 程序的名稱只會寫在 <c>PROCEDURE</c>／<c>PROC</c> 之後（<c>CREATE</c>、
    /// <c>CREATE OR ALTER</c>、<c>DROP</c> 都是）或 <c>EXEC</c>／<c>EXECUTE</c> 之後
    /// （含 <c>EXEC @rc = #p</c>）。呼叫也算數：程序可能是前一個工作階段建立的，
    /// 這份指令碼裡只剩呼叫。
    /// </remarks>
    private static IReadOnlyCollection<string> CollectTemporaryProcedures(IReadOnlyList<SqlToken> tokens)
    {
        List<string>? names = null;
        HashSet<string>? seen = null;

        for (var index = 1; index < tokens.Count; index++)
        {
            if (IsTemporaryName(tokens[index]) &&
                NamesProcedure(tokens, index) &&
                (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(tokens[index].Value))
            {
                (names ??= new List<string>()).Add(tokens[index].Value);
            }
        }

        return (IReadOnlyCollection<string>?)names ?? Array.Empty<string>();
    }

    private static bool NamesProcedure(IReadOnlyList<SqlToken> tokens, int name)
    {
        var previous = tokens[name - 1];

        if (previous.IsKeyword("PROCEDURE") || previous.IsKeyword("PROC") || IsExecute(previous))
        {
            return true;
        }

        return name >= 3 &&
               previous.Kind == SqlTokenKind.Operator &&
               previous.Value == "=" &&
               tokens[name - 2].Kind == SqlTokenKind.Variable &&
               IsExecute(tokens[name - 3]);
    }

    private static bool IsExecute(SqlToken token) => token.IsKeyword("EXEC") || token.IsKeyword("EXECUTE");

    /// <summary>
    /// 井號開頭、至少還有一個字元的識別字。
    /// </summary>
    /// <remarks>
    /// 單獨一個井號是使用者剛打出來、還沒打完的那一個，不是任何東西的名稱。
    /// </remarks>
    private static bool IsTemporaryName(SqlToken token)
    {
        return token.Kind == SqlTokenKind.Identifier &&
               token.Value.Length >= 2 &&
               token.Value[0] == '#';
    }

    private static SqlSuggestion DataSource(string name, string description, SqlScriptTable? table = null)
    {
        return new SqlSuggestion(
            name,
            name,
            description,
            ScriptSuggestionText.NameWithDescription(name, description),
            SuggestionKind.ScriptDataSource,
            tag: table);
    }
}
