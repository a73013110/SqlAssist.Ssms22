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
    Warning,
    Overflow,
    SelfTest
}
