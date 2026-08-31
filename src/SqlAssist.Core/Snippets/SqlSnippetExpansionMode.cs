namespace SqlAssist.Core.Snippets;

public enum SqlSnippetExpansionMode
{
    /// <summary>只插入文字並把游標移到 <c>$end$</c>。</summary>
    Caret,

    /// <summary>交給 SSMS 原生 Expansion Engine，以 Tab／Shift+Tab 導航欄位。</summary>
    TabStops
}
