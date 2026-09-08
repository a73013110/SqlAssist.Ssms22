using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Preview;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 建議清單裡選到的這一項，浮動預覽要拿它畫什麼。
/// </summary>
/// <remarks>
/// 資料庫物件直接掛在建議項上（<see cref="SqlSuggestion.Tag"/>），指令碼自己宣告的
/// 名稱卻沒有：中繼資料裡查不到的東西做不出 <see cref="SqlObjectInfo"/>，所以暫存
/// 資料表與資料表變數帶的是宣告本身，CTE 什麼都沒帶。少了這一層轉換的症狀是使用者
/// 在清單裡選到自己上一行才寫下的 <c>#Loan</c>，按向右鍵得到「沒有可以顯示的內容」。
///
/// 內建名稱同樣沒有物件可指，但它有一份隨組件發布的說明。認出它靠的是建議項自己
/// 帶的種類，不是從文字再猜一次——<c>YEAR</c> 在日期部分與內建函式目錄裡各有一筆，
/// 猜的話兩邊都說得通。
///
/// 這裡只從名稱認出它是哪一種，資料行留給
/// <see cref="SqlScriptDeclarations"/>：這條路徑在每一次換選取上，而使用者多半
/// 只是按著方向鍵路過，掃整份文字要等到真的有人要看結構才划算。
/// </remarks>
internal static class SqlSuggestionTarget
{
    /// <summary>沒有東西可畫的項目（關鍵字、片段、一般變數…）回傳 null。</summary>
    public static SqlPreviewSubject? Describe(SqlSuggestion suggestion)
    {
        if (suggestion is null)
        {
            return null;
        }

        if (suggestion.Tag is SqlObjectInfo objectInfo)
        {
            return SqlPreviewSubject.ForObject(objectInfo);
        }

        var name = suggestion.DisplayText;

        switch (suggestion.Kind)
        {
            // 資料表變數以外的變數沒有結構可看，而分辨的憑據就是它有沒有帶著宣告：
            // 讀不出資料行的 @readerId 在清單裡與 @rows 長得一模一樣。
            case SuggestionKind.Variable when suggestion.Tag is SqlScriptTable:
                return SqlPreviewSubject.ForObject(
                    new SqlObjectInfo(0, string.Empty, name, SqlScriptDeclarations.KindOf(name)));

            // 這份清單收三種：井號與小老鼠開頭的名稱由名稱本身分得出來，
            // 其餘的是 CTE。
            case SuggestionKind.ScriptDataSource:
                return SqlPreviewSubject.ForObject(new SqlObjectInfo(
                    0,
                    string.Empty,
                    name,
                    SqlIdentifier.IsScriptScoped(name)
                        ? SqlScriptDeclarations.KindOf(name)
                        : SqlObjectKind.CommonTableExpression));
        }

        // 說明面板已經給了一眼看得完的那一份，所以只有裝得滿一個視窗的才算——
        // 一個標題加一行說明的視窗，蓋掉的正是那一行說明本身。
        return SqlBuiltInKinds.TryFromSuggestionKind(suggestion.Kind, out var builtInKind) &&
               SqlBuiltInDocCatalog.TryGet(name, builtInKind, out var doc) &&
               doc.DeservesWindow
            ? SqlPreviewSubject.ForBuiltIn(doc)
            : null;
    }
}
