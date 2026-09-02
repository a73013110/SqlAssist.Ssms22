using System;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>游標處剛打完的那個字如果是關鍵字，改寫成標準大寫寫法。</summary>
public sealed class SqlKeywordRewrite
{
    public SqlKeywordRewrite(int start, int length, string replacement)
    {
        Start = start;
        Length = length;
        Replacement = replacement;
    }

    /// <summary>要被取代的範圍起點。</summary>
    public int Start { get; }

    /// <summary>要被取代的長度；與 <see cref="Replacement"/> 等長。</summary>
    public int Length { get; }

    /// <summary>取代後的文字。</summary>
    public string Replacement { get; }
}

/// <summary>
/// 關鍵字自動大寫。
/// </summary>
/// <remarks>
/// 使用者輸入 <c>select</c> 之後按下空白鍵就得到 <c>SELECT</c>，不必先按 Tab 提交建議。
/// 這條路徑刻意<b>不經過建議清單</b>：清單當下選中的可能是別的項目，
/// 用空白鍵提交清單選取項會把使用者根本不想要的名稱寫進編輯器。
/// 只改寫「剛打完的那個字」，行為完全可預測。
/// </remarks>
public static class SqlKeywordCase
{
    /// <summary>
    /// 判斷 <paramref name="position"/> 之前剛結束的字是否為關鍵字，是的話回報要如何改寫。
    /// </summary>
    /// <param name="text">整份文字。</param>
    /// <param name="position">即將輸入分隔字元的位置，也就是游標位置。</param>
    /// <param name="separator">
    /// 即將輸入的那個分隔字元；不知道時傳 <c>\0</c>，那時只認關鍵字。
    /// </param>
    /// <returns>不需要改寫時回傳 null。</returns>
    public static SqlKeywordRewrite? TryUppercaseWordBefore(
        string text,
        int position,
        char separator = '\0')
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return TryUppercaseWordBefore(new SqlStringText(text), position, separator);
    }

    /// <summary>
    /// 同上，但直接讀取文字來源。
    /// </summary>
    /// <remarks>
    /// 這個方法在每一次按下分隔字元時都會被呼叫，因此順序由便宜到昂貴：
    /// 先往回讀那幾個字元、查表確認是關鍵字，最後才做要掃過整份文字的語彙判斷。
    /// 打字時絕大多數按鍵在查表那一步就結束了。
    /// </remarks>
    public static SqlKeywordRewrite? TryUppercaseWordBefore(
        ISqlTextSource text,
        int position,
        char separator = '\0')
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (position <= 0 || position > text.Length)
        {
            return null;
        }

        var end = position;
        var start = position;

        while (start > 0 && IsWordCharacter(text[start - 1]))
        {
            start--;
        }

        if (start == end)
        {
            return null;
        }

        var word = text.Substring(start, end - start);

        if (!TryGetCanonical(word, separator, out var canonical))
        {
            return null;
        }

        // 已經是標準寫法就不要動：多一次編輯就多一個復原步驟。
        if (string.Equals(word, canonical, StringComparison.Ordinal))
        {
            return null;
        }

        // 限定字後面的名稱不是關鍵字：dbo.Select 是資料表名稱。
        if (start > 0 && text[start - 1] == '.')
        {
            return null;
        }

        // 字串、註解與括住的識別字裡面不能動。[Select] 是欄位名稱，'select' 是資料。
        if (!SqlLexicalContext.IsCode(text, start))
        {
            return null;
        }

        return new SqlKeywordRewrite(start, end - start, canonical);
    }

    /// <summary>
    /// 這個字後面接的字元決定它算不算一個該大寫的字。
    /// </summary>
    /// <remarks>
    /// 關鍵字不看分隔字元：<c>select</c> 後面接空白、逗號還是括號都一樣是關鍵字。
    ///
    /// 內建函式看。它們在文法上是<b>識別字</b>，ScriptDom 的 token 列舉裡一個都沒有
    /// （見 <see cref="SqlFunctionCatalog"/>），所以關鍵字目錄查不到——那正是
    /// <c>SELECT max( …</c> 的 <c>max</c> 一直沒有被改寫的原因。
    /// 但也不能因此就一律改寫：<c>SELECT max FROM t</c> 的 <c>max</c> 可能是一個
    /// 資料行的名字，在 CS 定序的資料庫上改掉它就是把查詢改壞。
    ///
    /// 分辨的依據只有一個而且很硬：左括號。<c>max(</c> 在 T-SQL 裡只能是呼叫，
    /// 不會是別的東西。用「在不在函式目錄裡」當依據不行——那份清單裡的
    /// <c>LEN</c>、<c>RANK</c>、<c>FORMAT</c> 全都是常見的資料行名稱。
    /// </remarks>
    private static bool TryGetCanonical(string word, char separator, out string canonical)
    {
        return SqlKeywordCatalog.TryGetCanonical(word, out canonical) ||
               (separator == '(' && SqlFunctionCatalog.TryGetCanonical(word, out canonical));
    }

    /// <summary>
    /// 這個字元是否會結束一個字。
    /// </summary>
    /// <remarks>
    /// 凡是不能構成識別字的字元都算：空白、逗號、括號、運算子與分號都會觸發改寫。
    /// </remarks>
    public static bool IsWordSeparator(char value) => !IsWordCharacter(value);

    /// <summary>
    /// 識別字可用的字元。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="SqlIdentifierScanner"/> 一致：底線、井號、小老鼠與錢號都算，
    /// 因此 <c>@select</c> 會被讀成一個變數名稱而不是關鍵字，不會被改寫。
    /// </remarks>
    private static bool IsWordCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '#' || value == '@' || value == '$';
    }
}
