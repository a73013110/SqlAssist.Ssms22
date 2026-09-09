using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Completion;

/// <summary>提交一個使用者自訂函式時，名稱後面要補到哪一步。</summary>
public enum SqlFunctionCallInsertionMode
{
    /// <summary>只寫名稱本身。</summary>
    None,

    /// <summary>補一對空括號，游標停在中間。</summary>
    Parentheses,

    /// <summary>補整組引數，依參數型別填預留值。</summary>
    Arguments
}

/// <summary>
/// 提交一個使用者自訂函式時補到哪一步，由這裡回答。
/// </summary>
/// <remarks>
/// 拆成兩個開關而不是一個，因為兩件事的取捨方向相反：括號在 T-SQL 裡不是選擇性的
/// （<c>SELECT dbo.fn_DueDate</c> 是語法錯誤，沒有參數的函式也一樣要寫 <c>()</c>），
/// 所以預設補；預留值只是猜出來的字面值，使用者接著要一個一個換掉，
/// 而括號一補上就換 SSMS 自己的參數資訊接手告訴他該填什麼，所以預設不補。
///
/// 判斷放在 Core 而不是 <c>SqlCommitExpander</c>，是因為兩個呼叫端要問的是同一件事：
/// 提交那一端要知道「要不要在插入文字後面補括號」，展開那一端要知道
/// 「要不要去中繼資料層拿參數」。各判斷一次的症狀是兩邊都補——
/// 括號寫進去了，引數又蓋上來一次。
/// </remarks>
public static class SqlFunctionCallInsertion
{
    /// <param name="isFunction">
    /// 被選中的是不是要寫成 <c>名稱(引數…)</c> 才呼叫得動的函式；
    /// 種類的判斷在中繼資料層（<c>SqlObjectKind.IsFunction</c>），那一層 Core 看不到。
    /// </param>
    /// <param name="target">
    /// 提交的位置在找什麼。<see cref="CompletionTarget.Function"/> 是
    /// <c>ALTER</c>／<c>DROP FUNCTION</c> 那個<b>宣告</b>位置，補上括號會讓那句 DDL
    /// 語法錯誤；其餘位置一律是呼叫。
    /// </param>
    public static SqlFunctionCallInsertionMode Resolve(
        bool isFunction,
        CompletionTarget target,
        SqlAssistSettings settings)
    {
        if (!isFunction ||
            target == CompletionTarget.Function ||
            settings is null ||
            !settings.ExpandFunctionCall)
        {
            return SqlFunctionCallInsertionMode.None;
        }

        return settings.ExpandFunctionArguments
            ? SqlFunctionCallInsertionMode.Arguments
            : SqlFunctionCallInsertionMode.Parentheses;
    }
}
