using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 不依賴連線就能提供的建議：T-SQL 關鍵字、內建函式與使用者的 Snippet。
/// </summary>
public static class BuiltInSuggestionCatalog
{
    /// <summary>每一個資料庫都有的兩個系統結構描述。</summary>
    private static readonly string[] SystemSchemas = { "sys", "INFORMATION_SCHEMA" };

    /// <summary>
    /// 建立候選清單。
    /// </summary>
    /// <remarks>
    /// 刻意沒有無參數的多載：Snippet 是使用者的資料，來源只有一個
    /// （<c>SqlSnippetStore</c>），少傳一個參數就安靜地少掉整批 Snippet
    /// 是很難查的錯。只要關鍵字時明確傳 <see cref="SqlSnippetLibrary.Empty"/>。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Create(SqlSnippetLibrary snippets)
    {
        if (snippets is null)
        {
            throw new ArgumentNullException(nameof(snippets));
        }

        var functions = SqlFunctionCatalog.All;
        var suggestions = new List<SqlSuggestion>(
            SqlKeywordCatalog.All.Count + functions.Count + snippets.Count + SystemSchemas.Length);

        // 這兩個結構描述在每一個資料庫裡都存在，是產品事實而不是誰的 schema，
        // 因此不必等中繼資料。第一層查詢刻意不收它們（那會連帶把一兩千個系統物件
        // 拉進來），少了這兩筆的話，使用者連「打 sys 再按 Tab」這條路都沒有。
        foreach (var schema in SystemSchemas)
        {
            suggestions.Add(new SqlSuggestion(
                schema,
                schema + ".",
                "Schema",
                $"Schema {schema}",
                SuggestionKind.Schema,
                triggerFollowUp: true,
                schemaName: schema));
        }

        foreach (var snippet in snippets.Snippets)
        {
            // 插入文字先把佔位符換成預設值：沒有游標標記、也沒有接續建議的
            // Snippet 就能完全交給平台插入，不必繞到自訂的提交路徑。
            var insertionText = snippet.Expand(out _);

            suggestions.Add(new SqlSuggestion(
                snippet.Shortcut,
                insertionText,
                snippet.Description,
                snippet.Title,
                SuggestionKind.Snippet,
                snippet.TriggerFollowUp,
                tag: snippet));
        }

        foreach (var keyword in SqlKeywordCatalog.All)
        {
            suggestions.Add(new SqlSuggestion(
                keyword,
                keyword,
                "T-SQL keyword",
                keyword,
                SuggestionKind.Keyword,
                positions: SqlKeywordCatalog.GetPositions(keyword)));
        }

        // 內建函式已經是不可變的建議項，這裡直接接上去就好。
        suggestions.AddRange(functions);

        return suggestions;
    }
}
