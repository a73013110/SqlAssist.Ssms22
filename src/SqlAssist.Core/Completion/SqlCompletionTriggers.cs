using System;
using SqlAssist.Core.Settings;

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
    /// 與建議來源是同一條規則（<see cref="SqlCompletionPolicy.Participates"/>）。
    /// 觸發字元數取最小值：走到這裡的字元不是識別字的一部分，前綴一定是空的
    /// ——小老鼠例外，但它的目標一律已經收斂——所以設定值影響不了結果。
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
        if (!MayChangeContext(textBeforeCaret[textBeforeCaret.Length - 1]))
        {
            return false;
        }

        return SqlCompletionPolicy.Participates(
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret),
            SqlAssistLimits.MinimumTriggerCharacters);
    }

    /// <summary>
    /// 剛輸入的這個字元有沒有可能把上下文整個換掉。
    /// </summary>
    /// <remarks>
    /// 識別字的字元只會把候選變少，平台自己的篩選是對的——<b>除了小老鼠</b>。
    /// 它雖然構得成識別字（<c>@@ROWCOUNT</c> 的詞元起點必須落在第一個小老鼠上），
    /// 但打出來的那一刻目標會整個換掉：<c>INSERT INTO </c> 開著的是資料表清單，
    /// 而 <c>INSERT INTO @</c> 要的是他自己宣告的變數，兩份沒有一項重疊。
    /// 平台只會拿新文字去比對舊清單，一個都比不中就默默把清單關掉——症狀正是
    /// 「單打一個 <c>@</c> 沒有清單，再多打一個字母才出現」。
    ///
    /// 判斷與 <see cref="ShouldReopen"/> 共用：呼叫端要用同一條規則決定要不要問，
    /// 各寫一份的話這裡放行了、那裡卻擋掉，等於沒改。
    /// </remarks>
    public static bool MayChangeContext(char value)
    {
        return !SqlCompletionContextAnalyzer.IsIdentifierCharacter(value) || value == '@';
    }
}
