using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Keywords;

namespace SqlAssist.CompletionAudit;

/// <summary>
/// 當成「字」稽核的詞：關鍵字、片語的字、資料型別、提示、日期部分與內建函式。
/// </summary>
/// <remarks>
/// 字的答案不看資料庫：它在清單上就對，不在就是漏。名稱則要知道它存在（<see cref="IAuditNameIndex"/>），
/// 否則語料裡使用者自己取的名字會被當成漏。
/// </remarks>
public static class AuditWords
{
    private static readonly Lazy<HashSet<string>> Words = new(Build);

    /// <summary><paramref name="word"/> 是清單上某一份目錄裡的字。</summary>
    public static bool Contains(string word) => Words.Value.Contains(word);

    /// <summary>
    /// 照慣例寫成全大寫的字：召回語料把字寫成大寫、名稱寫成大小寫混合。
    /// </summary>
    /// <remarks>
    /// 只認清單裡已有的字的話，哪一份清單都沒收的字（<c>SUBJECT</c>、<c>ALGORITHM</c>、<c>GENERATED</c>）
    /// 永遠不被稽核，而那正是最該抓的缺口。只用在守這個慣例的語料上：使用者的指令碼常把名稱寫成大寫。
    /// </remarks>
    public static bool IsWrittenAsWord(string text)
    {
        return text.Length >= 2 &&
            char.IsLetter(text[0]) &&
            text.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    }

    /// <summary>
    /// 大小寫混合的詞：召回語料的名稱。片語把 <c>COPY</c> 收成字之後，<c>Copy</c> 資料表仍是名稱；
    /// 全小寫的 <c>int</c>、<c>nvarchar</c> 照清單當字。
    /// </summary>
    public static bool IsMixedCase(string text) => text.Any(char.IsUpper) && text.Any(char.IsLower);

    private static HashSet<string> Build()
    {
        var words = new HashSet<string>(SqlKeywordCatalog.All, StringComparer.OrdinalIgnoreCase);

        // 片語尾巴裡的字面字也算：AT TIME 的 AT、NEXT VALUE 的 VALUE 不在任何一份清單的字裡。
        foreach (var phrase in SqlClausePhraseCatalog.All)
        {
            words.UnionWith(phrase.Words.SelectMany(word => word.Split(' ')));
            words.UnionWith(phrase.Pattern.Split(' ').Where(IsWordShaped));
        }

        foreach (var suggestion in SqlDataTypeCatalog.All
            .Concat(SqlArgumentCatalog.TableHints)
            .Concat(SqlArgumentCatalog.QueryHints)
            .Concat(SqlArgumentCatalog.DateParts)
            .Concat(SqlFunctionCatalog.All))
        {
            words.UnionWith(suggestion.DisplayText.Split(new[] { ' ', '(' }, StringSplitOptions.RemoveEmptyEntries));
        }

        return words;
    }

    private static bool IsWordShaped(string item) => item.Length > 0 && (char.IsLetter(item[0]) || item[0] == '_');
}
