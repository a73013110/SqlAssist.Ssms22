namespace SqlAssist.Core.Keywords;

/// <summary>位置旗標的比對規則。</summary>
public static class SqlKeywordPositionExtensions
{
    /// <summary>
    /// 帶著 <paramref name="positions"/> 的建議項，能不能出現在分析器回報的
    /// <paramref name="caret"/> 位置。
    /// </summary>
    /// <remarks>
    /// 一般情形是位元交集。<see cref="SqlKeywordPosition.None"/> 是唯一的例外：
    /// 產生器判不出位置的字只在分析器也判不出位置（<see cref="SqlKeywordPosition.Any"/>）
    /// 時出現。
    ///
    /// 以前 <c>None</c> 在讀進來時就換成 <c>Any</c>，於是 <c>FILLFACTOR</c>、
    /// <c>STOPLIST</c> 這些深層子句字在每一個判得出的位置都出現——<c>SELECT F</c>
    /// 的清單裡有 <c>FILLFACTOR</c>。反過來整個藏起來也不行：分析器判不出位置的地方
    /// 正是這些字真正的用處（<c>WITH (</c>、<c>= ANY</c>）。字的用法若落在判得出的位置，
    /// 該補的是產生器的樣板，不是放寬這一條。
    ///
    /// 關鍵字、內建函式與片段共用這一條；片段沒寫 <c>positions</c> 的意思是「哪裡都能用」，
    /// 讀進來就是 <see cref="SqlKeywordPosition.Any"/>，不會落到這個例外。
    /// </remarks>
    public static bool Allows(this SqlKeywordPosition positions, SqlKeywordPosition caret)
    {
        return positions == SqlKeywordPosition.None
            ? caret == SqlKeywordPosition.Any
            : (positions & caret) != SqlKeywordPosition.None;
    }
}
