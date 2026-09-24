namespace SqlAssist.Core.Lists;

public enum SqlListFooterKind
{
    /// <summary>不可用或第一頁載入中；第一頁由表面載入圖示表達，頁尾不佔位置。</summary>
    Hidden,

    /// <summary>已載入完畢且沒有任何項目。</summary>
    Empty,

    /// <summary>還有下一頁；捲到底或按下都會續頁。</summary>
    More,

    /// <summary>搜尋用盡單頁預算；必須由使用者明確續搜。</summary>
    ContinueSearch,

    /// <summary>已有項目、正在載入下一頁；進度留在原地，不遮住已載入的清單。</summary>
    Loading,

    /// <summary>全部載入完畢。</summary>
    End,
}

/// <summary>清單頁尾的呈現：UI 只照著畫，文案與狀態轉換由各清單的模型決定。</summary>
/// <remarks>
/// SQL Memory 的 History／Favorites、收藏版本時間軸與 SQL Search 的結果清單共用這一份：
/// 筆數、部分結果與續頁都說在清單的結尾，而不是另起一條狀態列。各功能只決定說什麼。
/// </remarks>
public sealed class SqlListFooter
{
    public SqlListFooter(SqlListFooterKind kind, string summary, string? hint = null, string? actionLabel = null)
    {
        Kind = kind;
        Summary = summary;
        Hint = hint;
        ActionLabel = actionLabel;
    }

    /// <summary>不佔位置的那一份；呼叫端把某一種狀態交給別的表面時用它蓋掉頁尾。</summary>
    public static SqlListFooter Hidden { get; } = new(SqlListFooterKind.Hidden, "");

    public SqlListFooterKind Kind { get; }

    /// <summary>頁尾中央的單行摘要，例如筆數。</summary>
    public string Summary { get; }

    /// <summary>摘要下方的淡色說明；沒有時為 null。</summary>
    public string? Hint { get; }

    /// <summary>頁尾按鈕文字；null 表示沒有按鈕。</summary>
    public string? ActionLabel { get; }

    /// <summary>按鈕是否可按；載入中的按鈕保留位置但停用。</summary>
    public bool CanAct => Kind is SqlListFooterKind.More or SqlListFooterKind.ContinueSearch;
}
