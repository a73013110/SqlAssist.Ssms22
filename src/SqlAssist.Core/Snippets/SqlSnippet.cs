using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Snippets;

/// <summary>Snippet 裡的一個佔位符。</summary>
public sealed class SqlSnippetPlaceholder
{
    public SqlSnippetPlaceholder(string id, string defaultValue = "", string toolTip = "")
    {
        Id = id ?? string.Empty;
        DefaultValue = defaultValue ?? string.Empty;
        ToolTip = toolTip ?? string.Empty;
    }

    /// <summary>在程式碼裡以 <c>$Id$</c> 引用。</summary>
    public string Id { get; }

    /// <summary>展開時先填進去的值。</summary>
    public string DefaultValue { get; }

    /// <summary>管理介面裡顯示的說明。</summary>
    public string ToolTip { get; }
}

/// <summary>
/// 一個可展開的程式碼片段。
/// </summary>
/// <remarks>
/// 格式是 SqlAssist 自己的 JSON，不是 SSMS 的 <c>.snippet</c> XML。兩者不互通，
/// 使用者在 SSMS「程式碼片段管理員」裡的內容不會出現在這裡。
///
/// 不可變：<see cref="SqlSnippetLibrary"/> 每次異動都換掉整個集合，
/// 讀取端（建議清單、展開器）拿到的永遠是一致的一份。
/// </remarks>
public sealed class SqlSnippet
{
    /// <summary>游標定位標記；展開後游標停在這裡，標記本身不會留在文字裡。</summary>
    public const string CaretMarker = "$end$";

    public SqlSnippet(
        string shortcut,
        string code,
        string title = "",
        string description = "",
        bool triggerFollowUp = false,
        IReadOnlyList<SqlSnippetPlaceholder>? placeholders = null)
    {
        Shortcut = shortcut ?? string.Empty;
        Code = code ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(title) ? Shortcut : title;
        Description = description ?? string.Empty;
        TriggerFollowUp = triggerFollowUp;
        Placeholders = placeholders ?? Array.Empty<SqlSnippetPlaceholder>();
    }

    /// <summary>輸入這串字就會展開；大小寫不敏感，整份清單裡必須唯一。</summary>
    public string Shortcut { get; }

    /// <summary>展開後插入的程式碼，可含 <c>$佔位符$</c> 與 <see cref="CaretMarker"/>。</summary>
    public string Code { get; }

    /// <summary>建議清單裡顯示的名稱。</summary>
    public string Title { get; }

    /// <summary>建議清單裡顯示的說明。</summary>
    public string Description { get; }

    /// <summary>
    /// 展開後是否立刻再彈一次建議清單。
    /// </summary>
    /// <remarks>
    /// 刻意只是布林，而不是「接著列哪一類物件」：那份清單的目標是由
    /// <see cref="SqlCompletionContextAnalyzer"/> 重新讀插入後的文字決定的——
    /// <c>SELECT * FROM </c> 之後看到 FROM 就只列資料來源。做成列舉會讓管理介面
    /// 出現一個選了也不會被採用的欄位。
    ///
    /// 因此要讓接續清單正確，靠的是程式碼結尾落在有意義的位置，不是這個旗標。
    /// </remarks>
    public bool TriggerFollowUp { get; }

    public IReadOnlyList<SqlSnippetPlaceholder> Placeholders { get; }

    /// <summary>
    /// 把佔位符換成預設值、移除游標標記之後的文字，以及游標該落在哪裡。
    /// </summary>
    /// <remarks>
    /// 沒有宣告的佔位符原樣保留：<c>$$</c> 之間的東西不見得是佔位符，
    /// 而把使用者寫的字默默吃掉遠比多留幾個錢字號難查。
    /// </remarks>
    public string Expand(out int caretOffset)
    {
        var text = Code;

        foreach (var placeholder in Placeholders)
        {
            text = text.Replace("$" + placeholder.Id + "$", placeholder.DefaultValue);
        }

        var marker = text.IndexOf(CaretMarker, StringComparison.Ordinal);

        if (marker < 0)
        {
            caretOffset = text.Length;
            return text;
        }

        caretOffset = marker;
        return text.Remove(marker, CaretMarker.Length);
    }
}
