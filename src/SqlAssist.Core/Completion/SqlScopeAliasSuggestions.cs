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
/// 相互關聯子查詢裡的外層別名也一樣。
///
/// 列的是每個來源的<b>限定字</b>：寫了別名就是別名；沒寫別名的由名稱限定，資料庫的資料表
/// 名稱清單裡本來就有，只存在於指令碼裡的 CTE 與暫存資料表沒有——症狀是
/// <c>FROM cte JOIN dbo.Loan AS l ON |</c> 只列得出 <c>l</c>。
/// </remarks>
public static class SqlScopeAliasSuggestions
{
    public static IReadOnlyList<SqlSuggestion> Create(SqlStatementScope scope, SqlColumnSourceResolver resolver)
    {
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        List<SqlSuggestion>? suggestions = null;
        HashSet<string>? seen = null;

        // OUTPUT 子句裡的 inserted、deleted 不寫在 FROM，解析時也排在最前面。
        foreach (var table in scope.ChangeTables)
        {
            (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(table.Alias!);
            (suggestions ??= new List<SqlSuggestion>()).Add(Create(table.Alias!, table));
        }

        // 由內往外：內層的別名遮住外層同名的那一個，與 TryResolve 解析的順序相同。
        for (var level = scope; level is not null; level = level.Outer)
        {
            foreach (var table in level.Tables)
            {
                var qualifier = string.IsNullOrEmpty(table.Alias)
                    ? IsScriptOnly(table, resolver) ? table.ObjectName : null
                    : table.Alias;

                if (qualifier is null ||
                    !(seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(qualifier))
                {
                    continue;
                }

                (suggestions ??= new List<SqlSuggestion>()).Add(Create(qualifier, table));
            }
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    /// <summary>
    /// 查詢 ORDER BY 的一項引用得到的選取清單別名（<c>SELECT CopyNo AS Seq … ORDER BY Seq</c>）；其餘位置是空的。
    /// </summary>
    /// <remarks>
    /// 它是這段查詢的輸出欄位，不是資料來源的限定字，所以列成欄位、說明與子查詢的欄位同一句。
    /// 哪裡引用得到見 <see cref="SqlColumnSourceResolver.FindOrderByAliases"/>。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> OrderByAliases(SqlColumnSourceResolver resolver, int tokenStart)
    {
        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        var aliases = resolver.FindOrderByAliases(tokenStart);

        if (aliases.Count == 0)
        {
            return Array.Empty<SqlSuggestion>();
        }

        var suggestions = new SqlSuggestion[aliases.Count];

        for (var index = 0; index < aliases.Count; index++)
        {
            var alias = aliases[index];

            suggestions[index] = new SqlSuggestion(
                alias,
                SqlIdentifier.QuoteIfNeeded(alias),
                ScriptSuggestionText.QueryResult,
                $"{ScriptSuggestionText.QueryResult}\r\n{alias}",
                SuggestionKind.Column);
        }

        return suggestions;
    }

    /// <summary>沒有別名、名稱只寫在指令碼裡的來源：CTE 與暫存資料表。</summary>
    /// <remarks>資料表變數不算：<c>@t.</c> 不是合法的限定字，那一格寫的是 <c>[@t]</c>，見變數建議。</remarks>
    private static bool IsScriptOnly(SqlTableReference table, SqlColumnSourceResolver resolver) =>
        !table.IsDerived &&
        table.SchemaName is null &&
        table.DatabaseName is null &&
        table.ServerName is null &&
        !string.IsNullOrEmpty(table.ObjectName) &&
        (table.ObjectName.StartsWith("#", StringComparison.Ordinal) ||
            resolver.FindCommonTableExpression(table.ObjectName) is not null);

    private static SqlSuggestion Create(string alias, SqlTableReference table)
    {
        var source = table.Path?.ToString() ?? table.ObjectName;
        var named = string.IsNullOrEmpty(table.Alias);
        var kind = !named
            ? SqlKindText.Alias
            : alias.StartsWith("#", StringComparison.Ordinal) ? SqlKindText.TemporaryTable : SqlKindText.CommonTableExpression;

        return new SqlSuggestion(
            alias,
            SqlIdentifier.QuoteIfNeeded(alias),
            kind,
            named || string.IsNullOrEmpty(source)
                ? ScriptSuggestionText.NameWithDescription(alias, kind)
                : ScriptSuggestionText.AliasOf(alias, source),
            SuggestionKind.Alias,
            tag: table);
    }
}
