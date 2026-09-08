using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;

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
    /// <remarks>
    /// 由 <see cref="SqlSnippetPlaceholders.EndId"/> 組出來，兩邊不會分岔。
    /// </remarks>
    public const string CaretMarker = "$" + SqlSnippetPlaceholders.EndId + "$";
    private readonly Lazy<SqlSnippetExpansion> _expansion;

    public SqlSnippet(
        string shortcut,
        string code,
        string title = "",
        string description = "",
        bool triggerFollowUp = false,
        IReadOnlyList<SqlSnippetPlaceholder>? placeholders = null,
        string id = "",
        SqlSnippetCategory category = SqlSnippetCategory.Other,
        bool isDestructive = false,
        SqlSnippetExpansionMode expansionMode = SqlSnippetExpansionMode.Caret,
        SqlKeywordPosition positions = SqlKeywordPosition.Any)
    {
        Id = id ?? string.Empty;
        Shortcut = shortcut ?? string.Empty;
        Code = code ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(title) ? Shortcut : title;
        Description = description ?? string.Empty;
        Placeholders = placeholders ?? Array.Empty<SqlSnippetPlaceholder>();
        Category = category;
        IsDestructive = isDestructive;
        ExpansionMode = expansionMode == SqlSnippetExpansionMode.TabStops && Placeholders.Count == 0
            ? SqlSnippetExpansionMode.Caret
            : expansionMode;
        TriggerFollowUp = ExpansionMode == SqlSnippetExpansionMode.Caret && triggerFollowUp;
        Positions = positions == SqlKeywordPosition.None ? SqlKeywordPosition.Any : positions;
        CanSurround = HasSurroundField(Placeholders);
        _expansion = new Lazy<SqlSnippetExpansion>(
            () => SqlSnippetExpansion.Create(this),
            isThreadSafe: true);
    }

    /// <summary>包夾用的衍生片段：欄位與展開結果都已經定案。</summary>
    /// <remarks>
    /// <see cref="Placeholders"/> 刻意原樣帶著。它描述的是<b>這一份樣板</b>有哪些欄位，
    /// 而包夾只是這一次展開時把其中一格填掉了；讀取端要的欄位一律取自
    /// <see cref="SqlSnippetExpansion.Fields"/>，兩者不會互相冒充。
    /// </remarks>
    private SqlSnippet(SqlSnippet source, SqlSnippetExpansion expansion)
    {
        Id = source.Id;
        Shortcut = source.Shortcut;
        Code = source.Code;
        Title = source.Title;
        Description = source.Description;
        Placeholders = source.Placeholders;
        Category = source.Category;
        IsDestructive = source.IsDestructive;
        Positions = source.Positions;

        // 已經填進去了就不能再包一次，接續建議也沒有意義：包夾之後游標落在
        // 使用者自己的程式碼旁邊，不是一句等著被接下去的半句話。
        CanSurround = false;
        TriggerFollowUp = false;

        // 包夾欄位不再是可導航欄位，所以只剩它一格的片段（be、trn）在這裡自然
        // 退化成單次插入——沒有欄位卻啟動原生 session 只會多一條按鍵路徑。
        ExpansionMode = expansion.Fields.Count == 0
            ? SqlSnippetExpansionMode.Caret
            : source.ExpansionMode;

        _expansion = new Lazy<SqlSnippetExpansion>(() => expansion, isThreadSafe: true);
    }

    /// <summary>跨版本不變的識別碼；內建片段的捷徑即使改名也靠它套用 override。</summary>
    public string Id { get; }

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

    public SqlSnippetCategory Category { get; }

    /// <summary>危險片段在沒有輸入前綴時不主動顯示。</summary>
    public bool IsDestructive { get; }

    public SqlSnippetExpansionMode ExpansionMode { get; }

    /// <summary>片段可以出現的 SQL 文法位置。</summary>
    public SqlKeywordPosition Positions { get; }

    /// <summary>樣板有包夾欄位，可以用來包住選取的文字。</summary>
    /// <remarks>
    /// 沒有另外宣告「這一筆支援包夾」的旗標：多一份宣告就多一個會與樣板分岔的
    /// 地方，而分岔的症狀是「清單裡列得出來，選了卻沒有把選取內容包進去」。
    /// </remarks>
    public bool CanSurround { get; }

    /// <summary>同一筆不可變片段只剖析一次，Completion 與原生 XML 共用結果。</summary>
    public SqlSnippetExpansion Expansion => _expansion.Value;

    /// <summary>
    /// 把佔位符換成預設值、移除游標標記之後的文字，以及游標該落在哪裡。
    /// </summary>
    /// <remarks>
    /// 沒有宣告的佔位符原樣保留：<c>$$</c> 之間的東西不見得是佔位符，
    /// 而把使用者寫的字默默吃掉遠比多留幾個錢字號難查。
    /// </remarks>
    public string Expand(out int caretOffset)
    {
        var expansion = Expansion;
        caretOffset = expansion.CaretOffset;
        return expansion.Text;
    }

    /// <summary>把選取的文字接進包夾欄位，得到這一次要插入的片段。</summary>
    /// <remarks>
    /// 不改動原片段：整份清單是不可變的共用參考（見 <see cref="SqlSnippetLibrary"/>），
    /// 而且同一筆片段可以在不同編輯器裡各包各的。衍生的這一份只活過一次插入，
    /// 原生 XML 的快取以片段本身為鍵，跟著它一起回收。
    /// </remarks>
    public SqlSnippet WithSurroundText(string? selectedText)
    {
        return new SqlSnippet(this, SqlSnippetExpansion.Create(this, selectedText ?? string.Empty));
    }

    private static bool HasSurroundField(IReadOnlyList<SqlSnippetPlaceholder> placeholders)
    {
        for (var index = 0; index < placeholders.Count; index++)
        {
            if (SqlSnippetPlaceholders.IsNamed(
                    placeholders[index].Id,
                    SqlSnippetPlaceholders.SurroundId))
            {
                return true;
            }
        }

        return false;
    }
}
