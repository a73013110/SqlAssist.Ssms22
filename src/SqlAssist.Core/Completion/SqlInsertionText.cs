using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 決定提交一筆建議時要寫進編輯器的文字。
/// </summary>
/// <remarks>
/// 只吃建議項、上下文與設定，三個都是純資料，所以整組情境都測得到；Ssms22 那一側
/// 只負責在建立項目與提交時呼叫它，並把結果交給編輯器。
/// </remarks>
public static class SqlInsertionText
{
    public static string Build(
        SqlSuggestion suggestion,
        SqlCompletionContext context,
        SqlAssistSettings settings)
    {
        if (CarriesOwnInsertionText(suggestion.Kind))
        {
            return suggestion.InsertionText;
        }

        var objectName = Quote(suggestion.DisplayText, settings);

        // 路徑的中間段只寫名稱本身，點號留給使用者自己打。
        //
        // 曾經連點號一起寫進去，想省一個按鍵並順便接著開下一段。那不對：提交一筆
        // 建議的意思是「我要這個名稱」，不是「我要繼續往下走」——選了資料庫想直接
        // 換行去寫別的、或想手動打結構描述的人，都得先退掉一個他沒要求的字元。
        // 接續的部分本來就有人做了：打出點號會讓上下文整個換掉，
        // SqlCompletionTriggers 因此重開清單，而那條路徑對每一段都一樣。
        //
        // 這一條也擋住「把結構描述限定到自己身上」：這幾類的 SchemaName 就是它們
        // 自己，掉進下面那段會寫出 dbo.dbo。
        if (suggestion.Kind is SuggestionKind.Schema
            or SuggestionKind.Database
            or SuggestionKind.LinkedServer)
        {
            return objectName;
        }

        if (!NeedsSchema(context, settings) ||
            string.IsNullOrWhiteSpace(suggestion.SchemaName))
        {
            return objectName;
        }

        return Quote(suggestion.SchemaName!, settings) + "." + objectName;
    }

    /// <summary>
    /// 這一類的插入文字在建立建議時就定案了，這裡原樣送出。
    /// </summary>
    /// <remarks>
    /// 欄位帶著必要的別名限定，內建函式帶著左括號，參數帶著 <c> = </c>，
    /// 三者都不能再套用物件用的結構描述規則。全域變數也在這裡：把
    /// <c>@@ROWCOUNT</c> 當成物件名稱去加方括號，寫進編輯器的會是
    /// <c>[@@ROWCOUNT]</c>。
    ///
    /// 寫成 <c>switch</c> 而不是一串 <c>||</c>：這個方法在建立清單時每一筆建議都
    /// 走一遍，列舉的 <c>switch</c> 讓編譯器有機會編成一次跳躍表而不是十一次比較；
    /// 新增一種建議時，該不該進這份名單也只有這裡要看。
    /// </remarks>
    private static bool CarriesOwnInsertionText(SuggestionKind kind)
    {
        return kind switch
        {
            SuggestionKind.Keyword => true,
            SuggestionKind.Snippet => true,
            SuggestionKind.Column => true,
            SuggestionKind.BuiltInFunction => true,
            SuggestionKind.GlobalVariable => true,
            SuggestionKind.Variable => true,
            SuggestionKind.DataType => true,
            SuggestionKind.Parameter => true,
            SuggestionKind.DatePart => true,
            SuggestionKind.TableHint => true,
            SuggestionKind.QueryHint => true,
            _ => false
        };
    }

    /// <summary>
    /// 這個位置要不要由插入文字自己補上結構描述。
    /// </summary>
    /// <remarks>
    /// 問的是限定字<b>停在哪一格</b>，不是「有沒有限定字」：
    ///
    /// <list type="bullet">
    /// <item>沒有限定字——補不補是偏好，交給 <c>QualifyObjectNames</c>。</item>
    /// <item>停在結構描述那一格——<c>dbo.</c> 已經寫了，而 <c>LibArchive..</c> 是
    /// 使用者用第二個點號說了「照預設解析」。兩種都不能再補，補了會寫出
    /// 四段式的 <c>LibArchive..[dbo].[Loan]</c>。</item>
    /// <item>停在資料庫那一格——<b>一定要補，而且不歸偏好管</b>。
    /// <c>LibArchive.Loan</c> 是兩段式，會被讀成「結構描述 LibArchive」，
    /// 而那個結構描述並不存在。理由與 <see cref="SqlIdentifier.QuoteIfNeeded"/>
    /// 那條一樣：關掉一個為了少打幾個字的偏好，不代表要產生無效語法。</item>
    /// </list>
    /// </remarks>
    private static bool NeedsSchema(SqlCompletionContext context, SqlAssistSettings settings)
    {
        return context.QualifierPath is { } path
            ? path.QualifierEnd == SqlQualifierSlot.Database
            : settings.QualifyObjectNames;
    }

    /// <summary>
    /// 依設定決定要不要加方括號。
    /// </summary>
    /// <remarks>
    /// 關掉「一律加方括號」只代表不想看到多餘的括號，不是要產生無效語法：
    /// 名稱含空白或保留字時仍必須加括號，這條由
    /// <see cref="SqlIdentifier.QuoteIfNeeded"/> 負責。展開萬用字元、
    /// 建立欄位建議時適用同一條規則，所以這個方法是公開的——那兩處在 Ssms22，
    /// 各自照設定再判斷一次就會分岔。
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
