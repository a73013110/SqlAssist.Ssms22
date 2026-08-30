using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 指令碼自己宣告的資料來源：CTE 與暫存資料表。
/// </summary>
/// <remarks>
/// 建議清單的資料庫物件全部來自中繼資料，而中繼資料只看得到目前連線資料庫的
/// <c>sys.objects</c>。CTE 只存在於這份指令碼裡，暫存資料表在 tempdb 裡，
/// 兩者都不在那份清單上——症狀是使用者上一行才寫下的名稱，下一行打 <c>FROM </c>
/// 卻一個建議都沒有，而那正是他最需要補字的時候（名稱是他剛取的，還沒背起來）。
///
/// 只在游標真的落在資料來源位置時才建立。掃描本身是單趟線性的，但這條路徑
/// 在每一次按鍵上，而絕大多數位置根本用不到這一份。
/// </remarks>
public static class SqlScriptDataSourceSuggestions
{
    private const string CommonTableExpressionDescription = "CTE";

    private const string TemporaryTableDescription = "暫存資料表";

    /// <summary>
    /// 組出這份指令碼宣告的資料來源。
    /// </summary>
    /// <param name="tokens">整份指令碼的詞法單元。</param>
    /// <param name="commonTableExpressionNames">
    /// CTE 名冊；由 <see cref="SqlColumnSourceResolver"/> 交出來，
    /// 與欄位解析共用同一次掃描的結果。
    /// </param>
    public static IReadOnlyList<SqlSuggestion> Create(
        IReadOnlyList<SqlToken> tokens,
        IEnumerable<string> commonTableExpressionNames)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (commonTableExpressionNames is null)
        {
            throw new ArgumentNullException(nameof(commonTableExpressionNames));
        }

        List<SqlSuggestion>? suggestions = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in commonTableExpressionNames)
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
                    Create(token.Value, TemporaryTableDescription));
            }
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    private static SqlSuggestion Create(string name, string description)
    {
        return new SqlSuggestion(
            name,
            name,
            description,
            $"{name}（{description}）",
            SuggestionKind.ScriptDataSource);
    }
}
