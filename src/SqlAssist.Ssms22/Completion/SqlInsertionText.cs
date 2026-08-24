using SqlAssist.Core;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 決定提交一筆建議時要寫進編輯器的文字。
/// </summary>
/// <remarks>
/// 兩種建議引擎共用同一份規則，否則切換引擎時插入結果會不一致。
/// </remarks>
internal static class SqlInsertionText
{
    public static string Build(
        SqlSuggestion suggestion,
        SqlCompletionContext context,
        SqlAssistSettings settings)
    {
        if (suggestion.Kind == SuggestionKind.Keyword || suggestion.Kind == SuggestionKind.Snippet)
        {
            return suggestion.InsertionText;
        }

        // 關掉「一律加方括號」只代表不想看到多餘的括號，不是要產生無效語法：
        // 名稱含空白或保留字時仍必須加括號，否則插入的 SQL 直接壞掉。
        var objectName = Quote(suggestion.DisplayText, settings);

        if (suggestion.Kind == SuggestionKind.Schema)
        {
            return objectName + ".";
        }

        if (context.Qualifier is not null ||
            !settings.Suggestions.QualifyObjectNames ||
            string.IsNullOrWhiteSpace(suggestion.SchemaName))
        {
            return objectName;
        }

        return Quote(suggestion.SchemaName!, settings) + "." + objectName;
    }

    private static string Quote(string name, SqlAssistSettings settings)
    {
        return settings.Suggestions.UseSquareBrackets
            ? SqlIdentifier.Quote(name)
            : SqlIdentifier.QuoteIfNeeded(name);
    }
}
