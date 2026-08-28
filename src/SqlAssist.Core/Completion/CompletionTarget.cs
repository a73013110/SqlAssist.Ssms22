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
    Database
}

