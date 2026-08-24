namespace SqlAssist.Core;

/// <summary>
/// 提交建議時應該做什麼。
/// </summary>
/// <remarks>
/// <c>ALTER PROCEDURE</c> 與 <c>EXEC</c> 後方都只該顯示預存程序，但提交行為完全不同：
/// 前者要放進可直接執行的完整定義，後者只要補上名稱。兩者必須分開表示。
/// </remarks>
public enum CompletionIntent
{
    /// <summary>只插入物件名稱。</summary>
    Reference,

    /// <summary>插入該模組可直接執行的完整 ALTER 定義。</summary>
    AlterDefinition
}
