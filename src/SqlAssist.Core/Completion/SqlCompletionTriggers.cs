using System;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 什麼時候該把建議清單重開一次。
/// </summary>
/// <remarks>
/// 平台的規則是「沒有 session 就問來源要不要開，已經有 session 就只重新篩選」。
/// 這對一般的識別字是對的：多打一個字母只是把候選變少。但**結束詞元的字元**不是——
/// 它會讓上下文整個換掉，而還開著的那份清單是照舊上下文組出來的：
///
/// <list type="bullet">
/// <item><c>SELECT a.</c> 從「關鍵字與物件」變成「a 的欄位」。</item>
/// <item><c>SELECT * FROM </c> 從「什麼都有」變成「只有資料表與檢視」。</item>
/// </list>
///
/// 兩種情形下平台都只會拿新文字去比對舊清單，比不中就默默把清單關掉，
/// 使用者得再多打一個字母才等到正確的清單。
///
/// 判斷刻意留在這裡而不是 SSMS 那一層：它只跟文字有關，可以完整單元測試。
/// </remarks>
public static class SqlCompletionTriggers
{
    /// <summary>
    /// 剛輸入的字元結束了一個詞元，而且新位置的上下文值得重開清單。
    /// </summary>
    /// <param name="textBeforeCaret">
    /// 游標<b>前方</b>的文字，且字元已經插入。刻意只要前半段：
    /// 判斷用不到游標後方的文字（見下），而每按一次分隔字元就把整份指令碼
    /// 複製一次，在幾千行的指令碼上是白付的代價。
    /// </param>
    /// <remarks>
    /// 判斷條件與建議來源的參與條件是同一個，只是這裡的前綴必然是空的：
    /// 來源在「目標是 <see cref="CompletionTarget.Any"/>、沒有限定字、
    /// 前綴短於觸發字元數」時不參與，而觸發字元數的最小值是 1，
    /// 空前綴永遠小於它。剩下的就是「有限定字」或「目標已經收斂」兩種情形。
    ///
    /// 因此這裡不需要讀設定：條件化簡之後與設定值無關，
    /// 而使用者把觸發字元數調大時，本來就只影響「開始輸入名稱之後」的行為。
    ///
    /// 也因此不需要看游標後方的文字。完整文字的多載只做一件事——把有限定字
    /// 而且解析得出別名的情形從 <see cref="CompletionTarget.Any"/> 改成
    /// <see cref="CompletionTarget.Column"/>；而「有限定字」這一支根本不看目標。
    /// 兩條路的結論一樣，就走便宜的那一條。
    /// </remarks>
    public static bool ShouldReopen(string textBeforeCaret)
    {
        if (textBeforeCaret is null)
        {
            throw new ArgumentNullException(nameof(textBeforeCaret));
        }

        if (textBeforeCaret.Length == 0)
        {
            return false;
        }

        // 還在打識別字時什麼都不用做：平台自己的篩選是對的。
        if (SqlCompletionContextAnalyzer.IsIdentifierCharacter(
                textBeforeCaret[textBeforeCaret.Length - 1]))
        {
            return false;
        }

        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        // IsValid 同時擋掉字串與註解裡的字元——那裡面沒有任何東西該被建議。
        if (!context.IsValid)
        {
            return false;
        }

        return context.Qualifier is null
            ? context.Target != CompletionTarget.Any
            : IsIdentifierLike(context.Qualifier);
    }

    /// <summary>
    /// 限定字看起來像不像識別字。
    /// </summary>
    /// <remarks>
    /// 擋的是數值字面值：<c>1.5</c> 的點號前面是 <c>1</c>，一樣是「限定字加點號」，
    /// 但使用者在打的是一個數字，不是在引用什麼東西。分不出來的代價是
    /// 每次輸入小數點都彈出整個資料庫的物件清單。
    /// </remarks>
    private static bool IsIdentifierLike(string qualifier)
    {
        if (qualifier.Length == 0)
        {
            return false;
        }

        var first = qualifier[0];
        return char.IsLetter(first) || first == '_' || first == '#';
    }
}
