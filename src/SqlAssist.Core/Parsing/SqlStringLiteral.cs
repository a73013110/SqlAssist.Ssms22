using System;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 單引號字串常值的寫法與讀法。
/// </summary>
/// <remarks>
/// 詞法器的 <see cref="SqlTokenKind.String"/> 詞元保留原文（含 <c>N</c> 前置詞與引號），
/// 要拿裡面的值的呼叫端都走這裡，不各自剝引號：漏掉 <c>''</c> 還原的那一份會把
/// <c>N'O''Brien'</c> 讀成兩個字串。
/// </remarks>
public static class SqlStringLiteral
{
    /// <summary>寫成字串常值；含 ASCII 以外的字元時加 <c>N</c> 前置詞。</summary>
    /// <remarks>
    /// 不一律加 <c>N</c>：<c>AT TIME ZONE 'Taipei Standard Time'</c> 是文件與手寫的樣子，
    /// 而沒有非 ASCII 字元的值在 varchar 與 nvarchar 之間沒有差別。有的話一定要加，
    /// 否則在與內容不同字碼頁的資料庫上會變成問號。
    /// </remarks>
    public static string Quote(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var quoted = "'" + value.Replace("'", "''") + "'";

        foreach (var character in value)
        {
            if (character > '\u007F')
            {
                return "N" + quoted;
            }
        }

        return quoted;
    }

    /// <summary>讀出已經關上的字串常值的值；<c>N</c> 前置詞可有可無。</summary>
    /// <returns>不是關上的字串常值（還在打、根本不是字串）時為 <c>false</c>。</returns>
    public static bool TryUnquote(string text, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var start = text[0] is 'N' or 'n' ? 1 : 0;

        if (text.Length - start < 2 || text[start] != '\'' || text[text.Length - 1] != '\'')
        {
            return false;
        }

        var body = text.Substring(start + 1, text.Length - start - 2);

        // 內文裡落單的引號代表字串在那裡就結束了，後面那一段不是它的值。
        if (body.Replace("''", string.Empty).IndexOf('\'') >= 0)
        {
            return false;
        }

        value = body.Replace("''", "'");
        return true;
    }
}
