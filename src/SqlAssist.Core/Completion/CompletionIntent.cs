namespace SqlAssist.Core.Completion;

/// <summary>
/// 提交建議時應該做什麼。
/// </summary>
/// <remarks>
/// <c>ALTER PROCEDURE</c> 與 <c>EXEC</c> 後方都只該顯示預存程序，但提交行為完全不同：
/// 前者要放進可直接執行的完整定義，後者要組出一句具名傳值的呼叫。目標相同、行為不同
/// 的位置不只這一組，因此提交行為必須與 <see cref="CompletionTarget"/> 分開表示。
/// </remarks>
public enum CompletionIntent
{
    /// <summary>只插入物件名稱。</summary>
    Reference,

    /// <summary>插入該模組可直接執行的完整 ALTER 定義。</summary>
    AlterDefinition,

    /// <summary>展開成完整的 <c>INSERT</c>：欄位清單加上對應的 <c>VALUES</c> 預留值。</summary>
    InsertStatement,

    /// <summary>
    /// 展開成完整的 <c>MERGE</c>：比對鍵、<c>UPDATE SET</c>、<c>INSERT</c> 與
    /// <c>VALUES</c> 一次填滿。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="InsertStatement"/> 分開而不是共用：目標同樣是一張資料表，
    /// 但 MERGE 要的是三份不同的欄位清單，而且要知道哪幾欄是比對鍵。
    /// </remarks>
    MergeStatement,

    /// <summary>展開成具名傳值的 <c>EXEC</c>，必要時補上 OUTPUT 參數的 <c>DECLARE</c>。</summary>
    ExecuteCall
}
