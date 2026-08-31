namespace SqlAssist.Core.Snippets;

/// <summary>管理介面的固定分類；JSON 使用成員名稱，無法辨識時落到 Other。</summary>
public enum SqlSnippetCategory
{
    Other,
    Select,
    Dml,
    Ddl,
    ControlFlow,
    Clause
}
