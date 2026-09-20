using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>結果列上的操作；按鈕以它為 Tag，不拿圖示或文字當識別。</summary>
internal enum SqlSearchRowAction
{
    /// <summary>把這一筆的定義開進新的查詢視窗。</summary>
    Activate,

    Copy,

    /// <summary>展開預覽並顯示這一筆；預覽收著的時候，停駐時這顆是唯一看得到它內容的路。</summary>
    Preview
}

/// <summary>
/// 一個結果列操作的外觀。卡片與快捷選單都從 <see cref="All"/> 建立，兩處不會漏掉或順序不一。
/// </summary>
/// <remarks>
/// 順序就是兩處的呈現順序：主要動作（移至定義）在前，其餘照使用頻率。
/// 與 SQL Memory 的 <c>SqlMemoryRowCommand</c> 分開兩份，是因為兩邊能做的事不一樣——
/// 併成一份就得在每一個操作上多掛一個「這一種列適不適用」，而那是同一件事做兩次。
/// </remarks>
internal sealed class SqlSearchRowCommand
{
    private SqlSearchRowCommand(SqlSearchRowAction action, SqlIcon icon, string label, bool primary = false)
    {
        Action = action;
        Icon = icon;
        Label = label;
        IsPrimary = primary;
    }

    public static IReadOnlyList<SqlSearchRowCommand> All { get; } = new[]
    {
        new SqlSearchRowCommand(SqlSearchRowAction.Activate, SqlIcon.Open, "移至定義", primary: true),
        new SqlSearchRowCommand(SqlSearchRowAction.Copy, SqlIcon.Copy, "複製限定名稱"),
        new SqlSearchRowCommand(SqlSearchRowAction.Preview, SqlIcon.Preview, "在預覽中顯示")
    };

    public SqlSearchRowAction Action { get; }

    public SqlIcon Icon { get; }

    public string Label { get; }

    /// <summary>這一列的主要動作；窄版只留它，其餘收進 overflow。</summary>
    public bool IsPrimary { get; }

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
        ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle(removable: false);
        ItemTemplate = SqlAssistChrome.CreateSearchHitTemplate();
    }

    /// <summary>綁上結果集合。</summary>
    /// <remarks>
    /// 只做一次：之後改的是集合內容，不是繫結。每次結果都重綁的話，捲動位置與選取會跟著整份換掉。
    /// </remarks>
    public void SetRowsSource(IEnumerable rows) => ItemsSource = rows ?? throw new ArgumentNullException(nameof(rows));

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+C 在清單上就是複製這一筆的限定名稱；使用者不必先展開預覽再去按那顆按鈕。
        if (!e.Handled && e.Key == Key.C && e.KeyboardDevice.Modifiers == ModifierKeys.Control && IsRowContent(e.OriginalSource))
        {
            e.Handled = true;
            RequestAction(SqlSearchRowAction.Copy);
        }

        base.OnPreviewKeyDown(e);
    }
}
