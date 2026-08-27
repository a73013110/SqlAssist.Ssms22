using System;
using SqlAssist.Core;

namespace SqlAssist.Metadata;

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
    /// 判斷識別字的字元形狀是否合乎一般識別字：開頭為字母或底線，
    /// 其餘為字母、數字、底線、井號、小老鼠或錢字號。
    /// </summary>
    /// <remarks>
    /// 這裡只看形狀，不看字義。<c>Order</c> 的形狀完全合格，但它是保留字，
    /// 不加括號寫出來仍然是語法錯誤——那一層判斷在 <see cref="QuoteIfNeeded"/>。
    /// </remarks>
    public static bool IsRegular(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!char.IsLetter(name[0]) && name[0] != '_')
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
