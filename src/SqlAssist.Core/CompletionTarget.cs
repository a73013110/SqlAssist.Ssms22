namespace SqlAssist.Core;

public enum CompletionTarget
{
    Any,
    DataSource,
    Procedure,
    Function,

    /// <summary>限定字解析成敘述中的資料來源，因此建議該來源的欄位。</summary>
    Column
}

