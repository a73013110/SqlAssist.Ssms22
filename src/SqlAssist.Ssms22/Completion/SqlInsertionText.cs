using SqlAssist.Core.Completion;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 決定提交一筆建議時要寫進編輯器的文字。
/// </summary>
internal static class SqlInsertionText
{
    public static string Build(
        SqlSuggestion suggestion,
        SqlCompletionContext context,
        SqlAssistSettings settings)
    {
        // 欄位的插入文字在建立建議時就決定好了（含必要的別名限定），
        // 內建函式則帶著左括號，兩者都不能再套用物件用的結構描述規則。
        // 全域變數也在這裡：把 @@ROWCOUNT 當成物件名稱去加方括號，
        // 寫進編輯器的會是 [@@ROWCOUNT]。
        if (suggestion.Kind == SuggestionKind.Keyword ||
            suggestion.Kind == SuggestionKind.Snippet ||
            suggestion.Kind == SuggestionKind.Column ||
            suggestion.Kind == SuggestionKind.BuiltInFunction ||
            suggestion.Kind == SuggestionKind.GlobalVariable ||
            suggestion.Kind == SuggestionKind.Variable ||
            suggestion.Kind == SuggestionKind.DataType ||
            suggestion.Kind == SuggestionKind.Parameter ||
            suggestion.Kind == SuggestionKind.DatePart ||
            suggestion.Kind == SuggestionKind.TableHint ||
            suggestion.Kind == SuggestionKind.QueryHint)
        {
            return suggestion.InsertionText;
        }

        var objectName = Quote(suggestion.DisplayText, settings);

        if (suggestion.Kind == SuggestionKind.Schema)
        {
            return objectName + ".";
        }

        if (context.Qualifier is not null ||
            !settings.QualifyObjectNames ||
            string.IsNullOrWhiteSpace(suggestion.SchemaName))
        {
            return objectName;
        }

        return Quote(suggestion.SchemaName!, settings) + "." + objectName;
    }

    /// <summary>
    /// 依設定決定要不要加方括號。
    /// </summary>
    /// <remarks>
    /// 關掉「一律加方括號」只代表不想看到多餘的括號，不是要產生無效語法：
    /// 名稱含空白或保留字時仍必須加括號，這條由
    /// <see cref="SqlIdentifier.QuoteIfNeeded"/> 負責。展開萬用字元、
    /// 建立欄位建議時適用同一條規則，所以這個方法開放給同組件使用。
    ///
    /// 反過來，開著「一律加方括號」也不代表什麼都包得下去：指令碼自己宣告的名稱
    /// 不在這個設定的管轄內（<see cref="SqlIdentifier.IsScriptScoped"/>）。
    /// <c>[#tmp]</c> 合法卻不是任何人會手寫的樣子，而 <c>[@rows]</c> 根本不合法。
    /// </remarks>
    public static string Quote(string name, SqlAssistSettings settings)
    {
        return settings.UseSquareBrackets && !SqlIdentifier.IsScriptScoped(name)
            ? SqlIdentifier.Quote(name)
            : SqlIdentifier.QuoteIfNeeded(name);
    }
}
