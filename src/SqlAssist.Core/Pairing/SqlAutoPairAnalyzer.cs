using System;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Pairing;

/// <summary>
/// 輸入分隔字元時要不要自動補上另一半。
/// </summary>
/// <remarks>
/// 五條規則各自獨立，呼叫端按「這一次是哪一種」擇一詢問：
///
/// <list type="bullet">
/// <item><see cref="AutoCloseFor"/>——打開頭字元，後面補上結尾字元。</item>
/// <item><see cref="ShouldOvertype"/>——打結尾字元，而它已經在游標右邊了。</item>
/// <item><see cref="IsEmptyPair"/>——Backspace 落在一對空的配對中間。</item>
/// <item><see cref="SurroundCloseFor"/>——有選取範圍時打開頭字元，包夾它。</item>
/// <item><see cref="InsertionCloseFor"/>——提交的建議自己帶了開頭字元。</item>
/// </list>
///
/// 前四條都在按鍵路徑上，因此順序刻意由便宜到昂貴：先看字元本身（一次比較），
/// 再看游標右邊那一個字元，最後才做需要從頭掃到游標的語彙狀態判斷。
/// 打字時絕大多數按鍵在第一步就結束。最後一條問在提交建議時，一次提交只問一遍。
///
/// 「這一次是不是我補的」不在這裡——那是編輯器的狀態，不是文字判斷。
/// 但 <see cref="ShouldOvertype"/> 與 <see cref="IsEmptyPair"/> 仍然留在這裡：
/// 呼叫端的兩個條件要分得開，才不會在 Ssms22 那一層寫出第二份字元規則。
/// </remarks>
public static class SqlAutoPairAnalyzer
{
    /// <summary>
    /// 在 <paramref name="position"/> 輸入 <paramref name="typedCharacter"/> 時要補上的結尾字元。
    /// </summary>
    /// <param name="position">
    /// 游標位置，而且 <paramref name="typedCharacter"/> <b>還沒</b>進入文字。
    /// 語彙狀態問的是這個位置，所以 <c>'abc|</c> 裡的引號會被認出還在字串中。
    /// </param>
    /// <returns>要補上的字元；這個位置不該配對時為 <c>null</c>。</returns>
    /// <remarks>
    /// 右邊必須是「一段文字的邊界」才補：<c>WHERE |Name = 1</c> 打左括號時，
    /// 使用者是要把後面那一段括起來，補上的右括號會夾在 <c>(|Name</c> 中間，
    /// 而他接著打的每一個字都在括號外面。這個判斷擋掉的正是那一種。
    /// </remarks>
    public static char? AutoCloseFor(ISqlTextSource sql, int position, char typedCharacter)
    {
        Validate(sql, position, nameof(position));

        if (!SqlDelimiterPairs.TryFromOpen(typedCharacter, out var pair))
        {
            return null;
        }

        if (!IsBoundaryAfter(sql, position))
        {
            return null;
        }

        // 字串、註解與識別字裡面不配對：那裡的括號與引號都是內容，不是語法。
        return SqlLexicalContext.IsCode(sql, position) ? pair.Close : null;
    }

    /// <summary>
    /// 提交建議寫進去的 <paramref name="insertionText"/> 以開頭字元結尾時，要補上的結尾字元。
    /// </summary>
    /// <param name="position">
    /// 要被換掉的那一段的終點，而 <paramref name="insertionText"/> <b>還沒</b>寫進文字。
    /// </param>
    /// <returns>要補上的字元；不該補時為 <c>null</c>。</returns>
    /// <remarks>
    /// 內建函式與帶參數的型別，插入文字本身就帶著左括號（<c>GETDATE(</c>、
    /// <c>varchar(</c>），而平台只會照著寫進去——右括號沒有人補的話，
    /// 提交完停在編輯器裡的是一句語法錯誤。條件與使用者自己打左括號完全相同，
    /// 所以問的是同一份規則。
    ///
    /// 兩端相同的配對（引號）不走這條：這裡的語彙狀態問的是插入<b>之前</b>的位置，
    /// 而引號是開是關要看它自己插進去之後的狀態，那兩個答案不一樣。
    /// </remarks>
    public static char? InsertionCloseFor(ISqlTextSource sql, int position, string insertionText)
    {
        Validate(sql, position, nameof(position));

        if (string.IsNullOrEmpty(insertionText) ||
            !SqlDelimiterPairs.TryFromOpen(insertionText[insertionText.Length - 1], out var pair) ||
            pair.Open == pair.Close)
        {
            return null;
        }

        if (!IsBoundaryAfter(sql, position))
        {
            return null;
        }

        return SqlLexicalContext.IsCode(sql, position) ? pair.Close : null;
    }

    /// <summary>
    /// 輸入的結尾字元就在游標右邊，應該跳過它而不是再插一個。
    /// </summary>
    /// <remarks>
    /// 這裡刻意不問語彙狀態：<c>'|'</c> 的游標位置在字串裡，而使用者要收掉的
    /// 正是那個字串。改問語彙狀態的話，引號永遠跳不過去。
    ///
    /// 也因此這一條單獨成立時還不夠——文字上分不出「這個右括號是我補的」還是
    /// 「使用者自己打的」，那要由呼叫端記住。少了那一半，游標停在既有的
    /// <c>)</c> 前面打右括號就再也插不進去。
    /// </remarks>
    public static bool ShouldOvertype(ISqlTextSource sql, int position, char typedCharacter)
    {
        Validate(sql, position, nameof(position));

        return SqlDelimiterPairs.IsClose(typedCharacter)
            && position < sql.Length
            && sql[position] == typedCharacter;
    }

    /// <summary>游標剛好夾在一對空的配對中間（<c>(|)</c>）。</summary>
    public static bool IsEmptyPair(ISqlTextSource sql, int position)
    {
        Validate(sql, position, nameof(position));

        return position > 0
            && position < sql.Length
            && SqlDelimiterPairs.TryFromOpen(sql[position - 1], out var pair)
            && pair.Close == sql[position];
    }

    /// <summary>
    /// 有選取範圍時輸入 <paramref name="typedCharacter"/>，用來包夾選取內容的結尾字元。
    /// </summary>
    /// <remarks>
    /// 不看選取範圍右邊有什麼：使用者已經明確指出要包哪一段，
    /// <see cref="AutoCloseFor"/> 那條邊界規則在這裡沒有意義。
    /// </remarks>
    public static char? SurroundCloseFor(ISqlTextSource sql, int selectionStart, char typedCharacter)
    {
        Validate(sql, selectionStart, nameof(selectionStart));

        if (!SqlDelimiterPairs.TryFromOpen(typedCharacter, out var pair))
        {
            return null;
        }

        return SqlLexicalContext.IsCode(sql, selectionStart) ? pair.Close : null;
    }

    /// <summary>
    /// 右邊是不是一段文字的邊界。
    /// </summary>
    /// <remarks>
    /// 逗號與分號算邊界，因為 <c>VALUES (1, |, 3)</c> 這種補完中間一格的寫法很常見；
    /// 右括號與右方括號算邊界，那是巢狀的入口（<c>fn(|)</c> 裡再打一層）。
    /// 其餘一律不算：識別字、數字與運算子的左邊都不該憑空多一個結尾字元。
    /// </remarks>
    private static bool IsBoundaryAfter(ISqlTextSource sql, int position)
    {
        if (position >= sql.Length)
        {
            return true;
        }

        var next = sql[position];

        return char.IsWhiteSpace(next)
            || next == ')'
            || next == ']'
            || next == ','
            || next == ';';
    }

    private static void Validate(ISqlTextSource sql, int position, string parameterName)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (position < 0 || position > sql.Length)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
