namespace SqlAssist.Core.Completion;

public enum CompletionTarget
{
    Any,
    DataSource,
    Procedure,
    Function,

    /// <summary>限定字解析成敘述中的資料來源，因此建議該來源的欄位。</summary>
    Column,

    /// <summary>
    /// <c>USE</c> 之後，建議這台伺服器上的資料庫。
    /// </summary>
    /// <remarks>
    /// 與其他目標不同，這裡要的東西不在目前連線的資料庫裡，而在伺服器層級。
    /// </remarks>
    Database,

    /// <summary>
    /// 游標停在 <c>@@</c> 開頭的詞元上，因此只建議 T-SQL 全域變數。
    /// </summary>
    /// <remarks>
    /// 目標由<b>正在輸入的詞元</b>決定，而不是由前導關鍵字決定，這是唯一的一個
    /// ——<c>@@</c> 開頭的名稱在 T-SQL 裡只有這一種意思，前面是什麼子句都不改變
    /// 這件事。也因此它與 <see cref="Database"/> 一樣可以跳過「輸入幾個字元之後
    /// 才建議」：使用者打完那兩個小老鼠時，要什麼已經說完了。
    /// </remarks>
    GlobalVariable
}

