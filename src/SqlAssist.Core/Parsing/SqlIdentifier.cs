using System;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Parsing;

public static class SqlIdentifier
{
    /// <summary>識別字最多幾個字元（<c>sysname</c>）。</summary>
    public const int MaximumLength = 128;

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
    /// 卻不是任何人會手寫的樣子——而資料表變數在 <c>FROM</c> 後面被寫成
    /// <c>[@rows]</c>，那會被讀成一張叫 <c>@rows</c> 的資料表，執行就找不到物件。
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
            if (!IsIdentifierCharacter(name[index]))
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
    /// 一致，而這裡的名稱一個都不是資料庫物件；資料表變數寫成 <c>[@rows]</c>
    /// 放在 <c>FROM</c> 後面更是直接指到一張不存在的資料表。
    /// 判斷放在這裡而不是設定那一層：它是名稱自己的性質，而問這個問題的表面
    /// 不會只有一個。
    /// </remarks>
    public static bool IsScriptScoped(string name)
    {
        return !string.IsNullOrEmpty(name) && (name[0] == '#' || IsVariable(name));
    }

    /// <summary>名稱是不是變數：小老鼠開頭，包含資料表變數。</summary>
    /// <remarks>
    /// 資料表變數在兩個位置要寫成兩個樣子，分界正是小老鼠：當資料來源時只能寫
    /// <c>@rows</c>，當欄位的限定字時只能寫 <c>[@rows].CopyNo</c>——<c>@rows.CopyNo</c>
    /// 會被讀成純量變數，執行起來是「必須宣告純量變數」。
    /// 井號不在這裡：<c>#Loan.CopyNo</c> 是合法的限定。
    /// </remarks>
    public static bool IsVariable(string name)
    {
        return !string.IsNullOrEmpty(name) && name[0] == '@';
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

    /// <summary>拿掉括住整個名稱的方括號或雙引號，並還原裡面的跳脫。</summary>
    /// <remarks>
    /// 只認<b>已經關上</b>的名稱；沒有括住時原樣回傳。還在打的那一種見
    /// <see cref="UnquoteOpening"/>：同一串 <c>[a]]</c>，當成關上的名稱會被剝掉結尾，
    /// 當成打到一半的則是使用者打的 <c>a]</c>。
    /// </remarks>
    public static string Unquote(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (text.Length >= 2 && text[0] == '[' && text[text.Length - 1] == ']')
        {
            return text.Substring(1, text.Length - 2).Replace("]]", "]");
        }

        if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
        {
            return text.Substring(1, text.Length - 2).Replace("\"\"", "\"");
        }

        return text;
    }

    /// <summary>
    /// 使用者在一個還沒關上的方括號裡打的名稱：拿掉開頭的左方括號並還原 <c>]]</c>。
    /// </summary>
    /// <remarks>
    /// 不是左方括號開頭時原樣回傳，所以一般的前綴可以直接丟進來。結尾的右方括號
    /// <b>不</b>拿掉：使用者自己打了右方括號就是這個名稱打完了，留著它讓比對落空，
    /// 清單才會跟著關掉。
    /// </remarks>
    public static string UnquoteOpening(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return text.Length > 0 && text[0] == '['
            ? text.Substring(1).Replace("]]", "]")
            : text;
    }

    /// <summary>
    /// 游標落在一個還沒關上、而且看得出是正在打的方括號名稱裡時，回傳左方括號的位置。
    /// </summary>
    /// <returns>左方括號的位置；不在方括號名稱裡時為 -1。</returns>
    /// <remarks>
    /// 詞法上左方括號一直延續到下一個右方括號，但「看得出正在打」要再加兩道限制，
    /// 否則一個忘了關的左方括號會讓底下整份指令碼都被當成一個名稱：
    /// 名稱不跨行，也不超過 <see cref="MaximumLength"/>。
    /// </remarks>
    public static int FindOpenBracket(string textBeforeCaret)
    {
        if (textBeforeCaret is null)
        {
            throw new ArgumentNullException(nameof(textBeforeCaret));
        }

        if (SqlLexicalContext.GetState(textBeforeCaret, textBeforeCaret.Length, out var start) !=
            SqlLexicalState.BracketedIdentifier ||
            textBeforeCaret.Length - start - 1 > MaximumLength)
        {
            return -1;
        }

        return textBeforeCaret.IndexOfAny(LineBreaks, start + 1) < 0 ? start : -1;
    }

    private static readonly char[] LineBreaks = { '\r', '\n' };

    /// <summary>
    /// 從 <paramref name="start"/> 起，同一個方括號名稱還剩下幾個字元（含右方括號）。
    /// </summary>
    /// <returns>右方括號之前只有識別字字元時是到右方括號為止的長度，否則為 0。</returns>
    /// <remarks>
    /// 提交時要一起換掉的那一段：自動配對補上的 <c>]</c>，或游標停在
    /// <c>[Lib|_Reader]</c> 中間時右邊那半個名稱。不換的話會寫出 <c>[Lib_Reader]]</c>。
    ///
    /// 只認識別字字元是刻意的：詞法上這個名稱延續到下一個右方括號，而那可能在
    /// 好幾個字之外——<c>[Lib| FROM t WHERE [x]</c> 照詞法換掉的是半句 SQL。
    /// 含空白的名稱在中間改字時因此會留下右半段，那比吃掉別人的文字好。
    /// </remarks>
    public static int MeasureClosingBracket(string text, int start)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        for (var index = start; index < text.Length; index++)
        {
            var value = text[index];

            if (value == ']')
            {
                return index - start + 1;
            }

            if (!IsIdentifierCharacter(value))
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>一般識別字允許的字元，開頭以外。</summary>
    internal static bool IsIdentifierCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '#' || value == '@' || value == '$';
    }
}
