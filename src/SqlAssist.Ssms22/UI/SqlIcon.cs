namespace SqlAssist.Ssms22.UI;

/// <summary>自製 UI 的語意圖示；每一項都對到 SSMS 原生影像目錄的一顆 moniker。</summary>
/// <remarks>
/// 名稱描述圖案的意思，不描述用在哪個模組：排序圖示叫 <see cref="SortAscending"/>，
/// 由呼叫端決定「最早」與「名稱 A–Z」都用它。對照在 <c>SqlIcons.GetMoniker(SqlIcon)</c>，
/// 那裡不寫預設分支，新增成員卻漏了對照會直接編譯失敗。
/// </remarks>
internal enum SqlIcon
{
    History,
    Favorite,
    All,
    Execute,
    Edit,
    Calendar,
    AnyTime,
    Server,
    Database,
    Connection,
    Search,
    Clear,
    MatchCase,
    WholeWord,
    Filter,
    SelectAll,
    Copy,
    Open,

    /// <summary>在別處的樹上把某一個節點指出來（物件總管）。</summary>
    Locate,

    Remove,
    Wrap,
    Refresh,
    Settings,
    SortAscending,
    SortDescending,
    SortByKind,
    Preview,
    Compare,
    Revert,
    Usage,
    Cleanup,
    Compact,
    Maintain,
    Backup,
    Folder,

    /// <summary>一般提醒；與 <see cref="Warning"/>、<see cref="Error"/> 是同一組狀態圖示。</summary>
    Information,

    Warning,
    Error,
    Overflow,

    /// <summary>往回一個命中；與 <see cref="NextMatch"/> 是一對。</summary>
    PreviousMatch,

    /// <summary>往後一個命中。</summary>
    NextMatch,

    SelfTest,

    /// <summary>動作完成的短暫回饋（複製成功時動作按鈕的圖示換成它）。</summary>
    Done
}
