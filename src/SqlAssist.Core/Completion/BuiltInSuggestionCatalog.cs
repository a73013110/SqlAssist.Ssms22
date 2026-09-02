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
            // 一般插入、原生 Expansion 與失敗降級都共用同一份剖析結果，
            // 否則錢字號與游標位置很容易在三條路上各有一種解讀。
            var expansion = snippet.Expansion;

            suggestions.Add(new SqlSuggestion(
                snippet.Shortcut,
                expansion.Text,
                snippet.Description,
                snippet.Title,
                SuggestionKind.Snippet,
                snippet.TriggerFollowUp,
                tag: snippet,
                positions: snippet.Positions,
                isDestructive: snippet.IsDestructive));
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
