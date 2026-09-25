using System;

namespace SqlAssist.Core.Completion;

/// <summary>兩次判斷之間發生過哪幾件事。</summary>
[Flags]
public enum SqlParameterHintEdits
{
    None = 0,

    /// <summary>打字、貼上、提交建議——插入文字的那一條路自己會請出提示。</summary>
    Inserted = 1,

    /// <summary>Backspace、Delete、剪下、復原打字。</summary>
    Deleted = 2,

    /// <summary>
    /// 別的路徑剛請過提示（提交函式時補上括號）。
    /// </summary>
    /// <remarks>
    /// 提交會把使用者打的前綴換掉，同一輪裡還可能夾著刪字；不記這一筆的話，
    /// 那一次提交之後會被請兩次，第二次把剛浮出來的提示收掉重畫。
    /// </remarks>
    Summoned = 4,
}

/// <summary>游標所在那個呼叫目前的狀態。</summary>
public enum SqlParameterHintCallState
{
    /// <summary>畫面上沒有提示時可以請。</summary>
    Open,

    /// <summary>
    /// 請過了，還沒看到任何一份提示浮出來。
    /// </summary>
    /// <remarks>
    /// <c>INSERT INTO t (</c>、查不到的名稱、使用者關掉了 SSMS 的參數資訊——這些括號請了
    /// 也不會有東西，每停一次手就再請一次只是白送命令。刪字才再試一次。
    /// </remarks>
    Awaiting,

    /// <summary>使用者在這個呼叫裡按了 Esc；離開那組括號之前都不請。</summary>
    Dismissed,
}

/// <summary>一次判斷的結果，以及下一次判斷要記住的東西。</summary>
public sealed class SqlParameterHintDecision
{
    public SqlParameterHintDecision(bool summon, int? call, SqlParameterHintCallState state)
    {
        Summon = summon;
        Call = call;
        State = state;
    }

    /// <summary>現在要不要把參數提示請回來。</summary>
    public bool Summon { get; }

    /// <summary>游標目前所在那個呼叫的左括號；不在任何呼叫裡時為 null。</summary>
    public int? Call { get; }

    /// <summary><see cref="Call"/> 那個呼叫的狀態。</summary>
    public SqlParameterHintCallState State { get; }
}

/// <summary>
/// 游標停在呼叫的括號裡、畫面上卻沒有參數提示時，要不要把它請回來。
/// </summary>
/// <remarks>
/// SSMS 的參數資訊只在<b>按鍵打出</b>左括號與逗號時浮出來；中途被收掉（刪字、滑鼠點擊、
/// 點出去再點回來、復原）就不會自己回來。這裡只回答「要不要請」，請的方式與內容都交給
/// 平台——沒有任何一份提示是自己畫的。
///
/// 規則以「畫面上有沒有提示」為準，而不是列舉會收掉它的操作：SSMS 什麼時候收掉它
/// 列不完（連在同一個字上點第二下都會），列舉一種漏一種。例外各自擋一種症狀：
/// <list type="bullet">
/// <item>換到另一個呼叫時開著的那一份講的是上一個函式，照請；但這一輪是打字走進來的
/// 就不請——打左括號、提交函式那一條自己請過。</item>
/// <item>請過卻沒浮出來（<see cref="SqlParameterHintCallState.Awaiting"/>）就停手，
/// 刪字才再試一次。</item>
/// <item>按過 Esc 的呼叫，離開之前都不請：那是使用者親手收掉的。</item>
/// </list>
///
/// 判斷刻意只吃位置與旗標：左括號的追蹤、去彈跳與送出命令都是平台那一層的事，
/// 這裡可以完整單元測試。
/// </remarks>
public static class SqlParameterHintRevival
{
    /// <summary>把緩衝區的一筆變更歸成哪一種編輯。</summary>
    /// <param name="deletedLength">這筆變更刪掉的字元數。</param>
    /// <param name="insertedText">這筆變更寫進去的文字。</param>
    /// <remarks>
    /// 單獨一個右括號不算插入：打完 <c>DATEDIFF(YEAR, GETDATE()</c> 的右括號，
    /// 游標回到外層的引數清單，與用方向鍵走回去是同一件事——內層那一份收掉了，
    /// 外層沒有人會請。自動配對跳過右括號的那一次連緩衝區都沒動，本來就只算游標移動。
    /// </remarks>
    public static SqlParameterHintEdits Classify(int deletedLength, string insertedText)
    {
        if (insertedText is null)
        {
            throw new ArgumentNullException(nameof(insertedText));
        }

        if (insertedText.Length == 0)
        {
            return deletedLength > 0 ? SqlParameterHintEdits.Deleted : SqlParameterHintEdits.None;
        }

        return deletedLength == 0 && insertedText == ")"
            ? SqlParameterHintEdits.None
            : SqlParameterHintEdits.Inserted;
    }

    /// <param name="previousCall">上一次判斷時所在呼叫的左括號，已對應到目前的文字；
    /// 中間離開過那組括號時傳 null。</param>
    /// <param name="previousState"><paramref name="previousCall"/> 那個呼叫的狀態。</param>
    /// <param name="currentCall">游標現在所在呼叫的左括號；不在任何呼叫裡時為 null。</param>
    /// <param name="edits">兩次判斷之間累積的編輯。</param>
    /// <param name="escaped">兩次判斷之間按過 Esc。</param>
    /// <param name="hintVisible">畫面上現在有沒有任何一份參數提示。</param>
    public static SqlParameterHintDecision Decide(
        int? previousCall,
        SqlParameterHintCallState previousState,
        int? currentCall,
        SqlParameterHintEdits edits,
        bool escaped,
        bool hintVisible)
    {
        // 離開所有括號：Esc 與請過的紀錄到此為止，下一次走進來就是新的一次。
        if (currentCall is not { } call)
        {
            return new SqlParameterHintDecision(false, null, SqlParameterHintCallState.Open);
        }

        var entered = previousCall != call;
        var state = entered ? SqlParameterHintCallState.Open : previousState;

        if (escaped || state == SqlParameterHintCallState.Dismissed)
        {
            return new SqlParameterHintDecision(false, call, SqlParameterHintCallState.Dismissed);
        }

        if (Has(edits, SqlParameterHintEdits.Summoned))
        {
            return new SqlParameterHintDecision(false, call, SqlParameterHintCallState.Awaiting);
        }

        var deleted = Has(edits, SqlParameterHintEdits.Deleted);
        var summon = entered
            ? deleted || !Has(edits, SqlParameterHintEdits.Inserted)
            : !hintVisible && (deleted || state == SqlParameterHintCallState.Open);

        if (summon)
        {
            return new SqlParameterHintDecision(true, call, SqlParameterHintCallState.Awaiting);
        }

        // 看得到提示就代表這個呼叫請得出來；之後它再被收掉時可以再請。
        return new SqlParameterHintDecision(
            false,
            call,
            hintVisible ? SqlParameterHintCallState.Open : state);
    }

    /// <summary>
    /// 有一份提示浮出來了（不論是誰請的）。
    /// </summary>
    /// <remarks>
    /// 請過的呼叫從此算「請得出來」，之後被收掉時可以再請；Esc 的效力不受影響——
    /// 使用者打逗號時 SSMS 自己浮出來的那一次，不代表他收回了 Esc。
    /// </remarks>
    public static SqlParameterHintCallState Shown(SqlParameterHintCallState state) =>
        state == SqlParameterHintCallState.Awaiting ? SqlParameterHintCallState.Open : state;

    private static bool Has(SqlParameterHintEdits edits, SqlParameterHintEdits flag) => (edits & flag) != 0;
}
