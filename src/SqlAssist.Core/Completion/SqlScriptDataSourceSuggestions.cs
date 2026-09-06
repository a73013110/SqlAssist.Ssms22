using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 指令碼自己宣告的資料來源：CTE、暫存資料表與資料表變數。
/// </summary>
/// <remarks>
/// 建議清單的資料庫物件全部來自中繼資料，而中繼資料只看得到目前連線資料庫的
/// <c>sys.objects</c>。CTE 與資料表變數只存在於這份指令碼裡，暫存資料表在 tempdb 裡，
/// 三者都不在那份清單上——症狀是使用者上一行才寫下的名稱，下一行打 <c>FROM </c>
/// 卻一個建議都沒有，而那正是他最需要補字的時候（名稱是他剛取的，還沒背起來）。
///
/// 只在游標真的落在資料來源位置時才建立。掃描本身是單趟線性的，但這條路徑
/// 在每一次按鍵上，而絕大多數位置根本用不到這一份。
/// </remarks>
public static class SqlScriptDataSourceSuggestions
{
    private const string CommonTableExpressionDescription = "CTE";

    private const string TemporaryTableDescription = "暫存資料表";

    private const string TableVariableDescription = "資料表變數";

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
    public static IReadOnlyList<SqlSuggestion> Create(
        IReadOnlyList<SqlToken> tokens,
        SqlColumnSourceResolver resolver)
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

        foreach (var name in resolver.CommonTableExpressionNames)
        {
            if (seen.Add(name))
            {
                (suggestions ??= new List<SqlSuggestion>()).Add(
                    Create(name, CommonTableExpressionDescription));
            }
        }

        // 暫存資料表不必分辨是哪一句建立的：井號開頭的識別字在 T-SQL 裡只有這一種
        // 意思，而 CREATE TABLE、SELECT INTO、INSERT INTO 各認一次的話，
        // 漏掉的那一種寫法就會安靜地少一個名稱。
        foreach (var token in tokens)
        {
            if (token.Kind != SqlTokenKind.Identifier ||
                token.Value.Length < 2 ||
                token.Value[0] != '#')
            {
                continue;
            }

            if (seen.Add(token.Value))
            {
                (suggestions ??= new List<SqlSuggestion>()).Add(
                    Create(token.Value, TemporaryTableDescription, resolver.FindScriptTable(token.Value)));
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
                    Create(table.Name, TableVariableDescription, table));
            }
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    private static SqlSuggestion Create(string name, string description, SqlScriptTable? table = null)
    {
        return new SqlSuggestion(
            name,
            name,
            description,
            $"{name}（{description}）",
            SuggestionKind.ScriptDataSource,
            tag: table);
    }
}
