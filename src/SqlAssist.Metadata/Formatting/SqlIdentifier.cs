using System;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Metadata.Formatting;

public static class SqlIdentifier
{
    /// <summary>以方括號括住識別字，內部的右方括號會被跳脫成 <c>]]</c>。</summary>
    public static string Quote(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>
    /// 判斷識別字的字元形狀是否合乎一般識別字：開頭為字母、底線、井號或小老鼠，
    /// 其餘為字母、數字、底線、井號、小老鼠或錢字號。
    /// </summary>
    /// <remarks>
    /// 這裡只看形狀，不看字義。<c>Order</c> 的形狀完全合格，但它是保留字，
    /// 不加括號寫出來仍然是語法錯誤——那一層判斷在 <see cref="QuoteIfNeeded"/>。
    ///
    /// 井號與小老鼠開頭是 T-SQL 明文允許的四種開頭裡的兩種，不是例外。
    /// 曾經把它們排除在外，症狀是暫存資料表被寫成 <c>[#tmp]</c>——那雖然合法，
    /// 卻不是任何人會手寫的樣子——而資料表變數被寫成 <c>[@rows]</c>，
    /// 那根本不是合法的 T-SQL，貼上去就是語法錯誤。
    /// </remarks>
    public static bool IsRegular(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!char.IsLetter(name[0]) && name[0] != '_' && !IsScriptScoped(name))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            var value = name[index];

            if (!char.IsLetterOrDigit(value) && value != '_' && value != '#' && value != '@' && value != '$')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 名稱是不是指令碼自己宣告的：暫存資料表（<c>#</c>、<c>##</c>）與
    /// 資料表變數（<c>@</c>）。
    /// </summary>
    /// <remarks>
    /// 這兩種名稱不受「一律加方括號」那個設定管轄。設定要的是資料庫物件寫起來
    /// 一致，而這裡的名稱一個都不是資料庫物件；<c>[@rows]</c> 更是直接的語法錯誤。
    /// 判斷放在這裡而不是設定那一層：它是名稱自己的性質，而問這個問題的表面
    /// 不會只有一個。
    /// </remarks>
    public static bool IsScriptScoped(string name)
    {
        return !string.IsNullOrEmpty(name) && (name[0] == '#' || name[0] == '@');
    }

    /// <summary>只有在必要時才加上方括號。</summary>
    /// <remarks>
    /// 「必要」有兩種，缺一種就會產生壞掉的 SQL：字元形狀不合（含空白、連字號、
    /// 開頭是數字），以及名稱本身是保留字。後者是 <c>Order</c>、<c>Key</c>、
    /// <c>User</c>、<c>Group</c> 這一類——形狀正常，直接插進去卻是語法錯誤。
    /// </remarks>
    public static string QuoteIfNeeded(string name)
    {
        return IsRegular(name) && !SqlKeywordCatalog.IsReservedIdentifier(name)
            ? name
            : Quote(name);
    }
}
