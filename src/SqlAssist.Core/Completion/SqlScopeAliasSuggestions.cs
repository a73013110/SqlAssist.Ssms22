using System;
using System.Collections.Generic;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 游標那一格看得到的別名：這一層的，加上外層查詢的。
/// </summary>
/// <remarks>
/// 欄位建議列的是「這一層的來源有哪些欄位」，但多個來源的敘述裡，使用者在
/// <c>ON |</c>、<c>WHERE |</c> 先打的是限定字——<c>target.</c>、<c>a.</c>。別名只寫在
/// 這一句裡，中繼資料與指令碼宣告的名冊都沒有它，少了這一份的症狀是
/// <c>MERGE … USING … AS source ON s</c> 列不出 <c>source</c>。
///
/// 範圍與別名解析是同一份 <see cref="SqlStatementScope"/>：列得出來的別名一定解析得回來源，
/// 相互關聯子查詢裡的外層別名也一樣。只列寫出來的別名，沒有別名的來源由資料表名稱
/// 限定，那個名稱清單裡本來就有。
/// </remarks>
public static class SqlScopeAliasSuggestions
{
    public static IReadOnlyList<SqlSuggestion> Create(SqlStatementScope scope)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        List<SqlSuggestion>? suggestions = null;
        HashSet<string>? seen = null;

        // 由內往外：內層的別名遮住外層同名的那一個，與 TryResolve 解析的順序相同。
        for (var level = scope; level is not null; level = level.Outer)
        {
            foreach (var table in level.Tables)
            {
                if (string.IsNullOrEmpty(table.Alias) ||
                    !(seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(table.Alias!))
                {
                    continue;
                }

                (suggestions ??= new List<SqlSuggestion>()).Add(Create(table.Alias!, table));
            }
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    private static SqlSuggestion Create(string alias, SqlTableReference table)
    {
        var source = table.Path?.ToString() ?? table.ObjectName;
        var kind = SqlKindText.Alias;

        return new SqlSuggestion(
            alias,
            SqlIdentifier.QuoteIfNeeded(alias),
            kind,
            string.IsNullOrEmpty(source)
                ? ScriptSuggestionText.NameWithDescription(alias, kind)
                : ScriptSuggestionText.AliasOf(alias, source),
            SuggestionKind.Alias,
            tag: table);
    }
}
