using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>結果列上的操作；按鈕以它為 Tag，不拿圖示或文字當識別。</summary>
internal enum SqlSearchRowAction
{
    /// <summary>把這一筆的定義開進新的查詢視窗。</summary>
    Activate,

    /// <summary>在物件總管上展開到這一筆並選取它。</summary>
    SelectInExplorer,

    /// <summary>把這一筆的限定名稱放上剪貼簿。</summary>
    Copy
}

/// <summary>
/// 一個結果列操作的外觀。卡片與快捷選單從 <see cref="All"/> 建立，預覽工具列從它篩出來的
/// <see cref="Preview"/> 建立，三處不會漏掉或順序不一。
/// </summary>
/// <remarks>
/// 順序就是三處的呈現順序：主要動作（移至定義）在前，其餘照使用頻率。
/// 沒有「在預覽中顯示」：左鍵點一下就是預覽，停駐時多一顆同義的按鈕只是讓其餘幾顆更難找。
/// 與 SQL Memory 的 <c>SqlMemoryRowCommand</c> 分開兩份，是因為兩邊能做的事不一樣——
/// 併成一份就得在每一個操作上多掛一個「這一種列適不適用」，而那是同一件事做兩次。
/// </remarks>
internal sealed class SqlSearchRowCommand
{
    /// <param name="availabilityPath">
    /// 列上那顆按鈕的 <c>IsEnabled</c> 要讀這一列的哪一個屬性；一律可用的操作傳 null。
    /// </param>
    /// <param name="labelPath">名稱隨列而變時讀哪一個屬性；固定用 <paramref name="label"/> 時傳 null。</param>
    /// <param name="toolTipPath">停駐那一顆的提示隨列而變時讀哪一個屬性；null 表示提示就是名稱。</param>
    /// <param name="inPreview">是否也排在預覽工具列上；理由見 <see cref="IsInPreview"/>。</param>
    private SqlSearchRowCommand(
        SqlSearchRowAction action,
        SqlIcon icon,
        Func<string> label,
        bool primary = false,
        string? availabilityPath = null,
        string? labelPath = null,
        string? toolTipPath = null,
        bool inPreview = true)
    {
        Action = action;
        Icon = icon;
        _label = label;
        IsPrimary = primary;
        AvailabilityPath = availabilityPath;
        LabelPath = labelPath;
        ToolTipPath = toolTipPath;
        IsInPreview = inPreview;
    }

    public static IReadOnlyList<SqlSearchRowCommand> All { get; } = new[]
    {
        new SqlSearchRowCommand(
            SqlSearchRowAction.Activate, SqlIcon.Open, () => Search.SqlSearchText.Activate, primary: true,
            availabilityPath: nameof(Search.SqlSearchRow.CanActivate),
            labelPath: nameof(Search.SqlSearchRow.ActivateLabel),
            toolTipPath: nameof(Search.SqlSearchRow.ActivateToolTip)),
        new SqlSearchRowCommand(
            SqlSearchRowAction.SelectInExplorer, SqlIcon.Locate, () => Search.SqlSearchText.SelectInExplorer,
            availabilityPath: nameof(Search.SqlSearchRow.CanSelectInExplorer)),
        new SqlSearchRowCommand(SqlSearchRowAction.Copy, SqlIcon.Copy, () => Search.SqlSearchText.CopyQualifiedName, inPreview: false)
    };

    /// <summary>預覽工具列上的列操作：<see cref="All"/> 裡 <see cref="IsInPreview"/> 的那幾個，順序不變。</summary>
    public static IReadOnlyList<SqlSearchRowCommand> Preview { get; } = All.Where(command => command.IsInPreview).ToArray();

    public SqlSearchRowAction Action { get; }

    public SqlIcon Icon { get; }

    private readonly Func<string> _label;

    public string Label => _label();

    /// <summary>這一列的主要動作；窄版只留它，其餘收進 overflow。</summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// 停駐時那顆按鈕的可用性讀哪一個屬性；null 表示一律可用。
    /// </summary>
    /// <remarks>
    /// 做成繫結路徑而不是在樣板裡判斷：樣板是每一列共用的一份，而「做不做得到」是每一列
    /// 自己的事。少了它的症狀正是右鍵選單上那一項已經變灰，停駐時的同一顆卻按得下去，
    /// 按了只得到一句「這一筆沒有…」。
    /// </remarks>
    public string? AvailabilityPath { get; }

    /// <summary>
    /// 名稱隨列而變時讀這一列的哪一個屬性；null 表示一律是 <see cref="Label"/>。
    /// </summary>
    /// <remarks>
    /// 「移至定義」在結果不在查詢視窗那一台時會開未連線的視窗，名稱上要先說。與
    /// <see cref="AvailabilityPath"/> 同一個道理做成繫結路徑：快捷選單與停駐那一顆讀同一個值，
    /// 各寫一次的症狀是選單說未連線、停駐那一顆的提示卻沒說。
    /// </remarks>
    public string? LabelPath { get; }

    /// <summary>停駐那一顆的提示讀這一列的哪一個屬性；null 表示提示就是名稱。</summary>
    public string? ToolTipPath { get; }

    /// <summary>是否也排在預覽工具列上。</summary>
    /// <remarks>
    /// 預覽工具列上的複製作用在畫面上那一份定義；「複製限定名稱」作用在列本身，放進去就是兩顆同一個
    /// 圖示的複製並排，只剩提示分得出來。名稱在清單上本來就拿得到（停駐、快捷選單、Ctrl+C），
    /// 所以預覽不放它，而不是替兩顆各找一個讀不出差別的圖示。
    /// </remarks>
    public bool IsInPreview { get; }

    [Localizable(false)]
    public static SqlSearchRowCommand For(SqlSearchRowAction action)
    {
        foreach (var command in All) if (command.Action == action) return command;
        throw new ArgumentOutOfRangeException(nameof(action), action, "沒有這個結果列操作。");
    }
}

/// <summary>
/// 搜尋結果清單：一疊平的列，命中部位由每一列自己的徽章表達。
/// </summary>
/// <remarks>
/// 不再分組。分組把同一批結果切成「名稱」「欄位」「定義本文」三疊，而使用者要找的那一筆
/// 可能在第三疊的底下——在停靠面板裡那等於看不到。改成每一列掛一顆徽章，並把排序交給使用者
/// （相關度／名稱／種類）；少了 <c>CollectionViewSource</c> 的分組，虛擬化也不再需要
/// <c>IsVirtualizingWhenGrouping</c> 這種容易漏掉的開關。
///
/// 開啟是明確動作（雙擊或 Enter），不是選取的副作用；選取只換預覽。與 SQL Memory 的清單
/// 同一條規則，理由也相同：選取會被方向鍵連續觸發。
/// </remarks>
internal sealed class SqlSearchList : SqlCardListBase<SqlSearchRowAction>
{
    public SqlSearchList()
    {
        // 結果沒有刪除動作；留著 IsRemoving 的繫結只會在每一列上找一個不存在的屬性。
        ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle(removable: false, checkable: true);
        ItemTemplate = SqlAssistChrome.CreateSearchHitTemplate();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+C 在清單上就是複製這一筆的限定名稱；使用者不必先展開預覽再去按那顆按鈕。
        // 多選模式中讓給選取的複製動作（由基底的快捷鍵派送），複製的是勾起來的那幾筆。
        if (!e.Handled && e.Key == Key.C && e.KeyboardDevice.Modifiers == ModifierKeys.Control &&
            Selection is not { IsActive: true } && IsRowContent(e.OriginalSource))
        {
            e.Handled = true;
            RequestAction(SqlSearchRowAction.Copy);
        }

        base.OnPreviewKeyDown(e);
    }
}
