using System.Collections.Generic;
using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 游標前面比對到的子句片語，以及這個比對有多確定。
/// </summary>
/// <remarks>
/// 片語帶著前一格的位置（<see cref="SqlClausePhrase.After"/>）時，比對要問位置分析那一格是什麼。
/// 判得出來而且對得上是<b>確定</b>的：這一格的關鍵字只來自片語。判不出來
/// （<see cref="SqlKeywordPosition.Any"/>）時只是<b>可能</b>：片語的字加進位置的關鍵字，
/// 不換掉它們，也不封閉清單。
///
/// 兩邊都有代價，選的是少不了字的那一邊：<c>DECLARE c CURSOR LOCAL FOR </c> 的前一格判不出位置，
/// 當成查詢之後的 FOR 並封閉清單的話只剩 XML、JSON，要的 SELECT 反而不見；
/// 反過來整個不算，<c>ORDER BY a DESC FOR </c> 又列不出 XML。
/// </remarks>
public sealed class SqlClausePhraseMatch
{
    internal SqlClausePhraseMatch(SqlClausePhrase phrase, bool isCertain)
    {
        Phrase = phrase;
        IsCertain = isCertain;
    }

    /// <summary>比對到的片語。</summary>
    public SqlClausePhrase Phrase { get; }

    /// <summary>前一格的位置判得出來，而且是片語要的那一種。</summary>
    public bool IsCertain { get; }

    /// <summary>清單只有片語的字：片語封閉，而且比對是確定的。</summary>
    public bool IsClosed => IsCertain && Phrase.IsClosed;

    /// <summary>片語的字的建議項。</summary>
    public IReadOnlyList<SqlSuggestion> Suggestions => Phrase.Suggestions;

    /// <summary>
    /// 目錄裡的關鍵字 <paramref name="keyword"/> 在這一格要不要讓給片語。
    /// </summary>
    /// <remarks>
    /// 確定時全部讓出；可能時只讓出片語裡也有的那幾個，同一個字才不會列兩次。
    /// </remarks>
    internal bool Hides(string keyword) => IsCertain || Phrase.Offers(keyword);
}
