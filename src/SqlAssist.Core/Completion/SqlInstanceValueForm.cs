namespace SqlAssist.Core.Completion;

/// <summary>執行個體名單上的值寫進指令碼的樣子。</summary>
/// <remarks>
/// 三種都由剖析器驗過：同一個名稱在三個位置能寫的樣子不一樣，寫錯就是一句執行不了的 SQL，
/// 而畫面上看不出差別。規則在 <see cref="SqlInsertionText.InstanceListValue"/>。
/// </remarks>
public enum SqlInstanceValueForm
{
    /// <summary>
    /// 照原樣，從不加方括號：<c>COLLATE [Latin1_General_CI_AS]</c> 是語法錯誤。
    /// 定序名稱全是一般識別字，不需要括號。
    /// </summary>
    Bare,

    /// <summary>
    /// 識別字，形狀不合或是保留字時才加方括號（<c>[Português (Brasil)]</c>）。
    /// 語言在 <c>SET LANGUAGE</c> 之後也收字串，但 <c>DEFAULT_LANGUAGE =</c> 之後只收識別字，
    /// 兩個位置都寫得進去的只有這一種。
    /// </summary>
    Identifier,

    /// <summary>
    /// 字串常值：<c>AT TIME ZONE</c> 之後的識別字是資料行參考（<c>AT TIME ZONE UTC</c>
    /// 剖析得過，執行時是「無效的資料行名稱」）。
    /// </summary>
    String
}
