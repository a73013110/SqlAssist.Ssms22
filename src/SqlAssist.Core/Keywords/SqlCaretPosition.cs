using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 位置分析的結果：這一格接哪些關鍵字、這一格是不是名字，以及游標前面是哪一個子句片語。
/// </summary>
/// <remarks>
/// 三件事在同一趟分析裡算出來，所以一起回傳。以前名字的位置借用
/// <see cref="SqlKeywordPosition.None"/> 表示，而那個值同時是「產生器判不出位置的字」。
/// </remarks>
public readonly struct SqlCaretPosition
{
    public SqlCaretPosition(
        SqlKeywordPosition keywords,
        SqlCompletionSlot slot = SqlCompletionSlot.Grammar,
        SqlClausePhrase? phrase = null)
    {
        Keywords = keywords;
        Slot = slot;
        Phrase = phrase;
    }

    /// <summary>
    /// 文法允許的關鍵字位置；判不出來時是 <see cref="SqlKeywordPosition.Any"/>。
    /// </summary>
    /// <remarks>
    /// <see cref="Slot"/> 是名字時，這裡是名字<b>寫完之後</b>接得了的位置：
    /// <c>FROM dbo.T AS </c> 的別名之後是資料來源尾端。
    /// </remarks>
    public SqlKeywordPosition Keywords { get; }

    /// <summary>這一格要的是不是使用者自己取的名字。</summary>
    public SqlCompletionSlot Slot { get; }

    /// <summary>
    /// 游標前面比對到的子句片語；比對到時，這一格的關鍵字只來自它，不看 <see cref="Keywords"/>。
    /// </summary>
    public SqlClausePhrase? Phrase { get; }
}
