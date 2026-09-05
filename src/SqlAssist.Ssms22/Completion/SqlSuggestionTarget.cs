using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 建議清單裡選到的這一項指向哪個物件。
/// </summary>
/// <remarks>
/// 資料庫物件直接掛在建議項上（<see cref="SqlSuggestion.Tag"/>），指令碼自己宣告的
/// 名稱卻沒有：中繼資料裡查不到的東西做不出 <see cref="SqlObjectInfo"/>，所以暫存
/// 資料表與資料表變數帶的是宣告本身，CTE 什麼都沒帶。少了這一層轉換的症狀是使用者
/// 在清單裡選到自己上一行才寫下的 <c>#Loan</c>，按向右鍵得到「不是資料庫物件」。
///
/// 這裡只從名稱認出它是哪一種，資料行留給
/// <see cref="SqlScriptDeclarations"/>：這條路徑在每一次換選取上，而使用者多半
/// 只是按著方向鍵路過，掃整份文字要等到真的有人要看結構才划算。
/// </remarks>
internal static class SqlSuggestionTarget
{
    /// <summary>沒有結構可看的項目（關鍵字、片段、一般變數…）回傳 null。</summary>
    public static SqlObjectInfo? Describe(SqlSuggestion suggestion)
    {
        if (suggestion is null)
        {
            return null;
        }

        if (suggestion.Tag is SqlObjectInfo objectInfo)
        {
            return objectInfo;
        }

        var name = suggestion.DisplayText;

        return suggestion.Kind switch
        {
            // 資料表變數以外的變數沒有結構可看，而分辨的憑據就是它有沒有帶著宣告：
            // 讀不出資料行的 @readerId 在清單裡與 @rows 長得一模一樣。
            SuggestionKind.Variable when suggestion.Tag is SqlScriptTable =>
                new SqlObjectInfo(0, string.Empty, name, SqlScriptDeclarations.KindOf(name)),

            // 這份清單只收兩種：井號開頭的暫存資料表，其餘的是 CTE。
            SuggestionKind.ScriptDataSource => new SqlObjectInfo(
                0,
                string.Empty,
                name,
                SqlIdentifier.IsScriptScoped(name)
                    ? SqlScriptDeclarations.KindOf(name)
                    : SqlObjectKind.CommonTableExpression),

            _ => null
        };
    }
}
