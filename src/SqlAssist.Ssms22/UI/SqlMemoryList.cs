using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace SqlAssist.Ssms22.UI;

/// <summary>卡片、快捷選單與 Preview 共用的列操作；按鈕以它為 Tag，不拿圖示名稱當動作識別。</summary>
internal enum SqlMemoryRowAction { Copy, Open, AddFavorite, Edit, Revisions, Delete }

/// <summary>操作適用於哪一種列；卡片模板與快捷選單都依它隱藏，不用停用的灰色按鈕佔位。</summary>
internal enum SqlMemoryRowKind { Any, History, Favorite }

/// <summary>
/// 一個列操作的外觀與適用範圍。卡片、快捷選單與 Preview 都從 <see cref="SqlMemoryRowCommand.All"/> 建立，
/// 新增操作只加一筆，三處不會各自漏掉或順序不一。
/// </summary>
/// <remarks>
/// <see cref="All"/> 的順序就是三處的呈現順序，動線固定為「主要動作 → 一般安全操作 → 收藏管理 → 破壞性操作」；
/// 呼叫端只能整段跳過不適用的項目，不得自己重排，否則同一批按鈕在卡片與 Preview 又會對不起來。
/// </remarks>
internal sealed class SqlMemoryRowCommand
{
    private SqlMemoryRowCommand(SqlMemoryRowAction action, SqlIcon icon, string label, SqlMemoryRowKind kind,
        string? labelProperty = null, bool separated = false, SqlActionTone tone = SqlActionTone.Neutral,
        bool primary = false)
    {
        Action = action; Icon = icon; Label = label; Kind = kind;
        LabelProperty = labelProperty; IsSeparated = separated; Tone = tone; IsPrimary = primary;
    }

    public static IReadOnlyList<SqlMemoryRowCommand> All { get; } = new[]
    {
        new SqlMemoryRowCommand(SqlMemoryRowAction.Open, SqlIcon.Open, "在新 Query 開啟（不執行）", SqlMemoryRowKind.Any,
            primary: true),
        new SqlMemoryRowCommand(SqlMemoryRowAction.Copy, SqlIcon.Copy, "複製 SQL", SqlMemoryRowKind.Any),
        new SqlMemoryRowCommand(SqlMemoryRowAction.AddFavorite, SqlIcon.Favorite, "新增至收藏", SqlMemoryRowKind.History,
            tone: SqlActionTone.Favorite),
        // 名稱、標註與 SQL 在同一個編輯器一次儲存，不拆成兩個各自做版本檢查的對話框。
        new SqlMemoryRowCommand(SqlMemoryRowAction.Edit, SqlIcon.Edit, "編輯收藏", SqlMemoryRowKind.Favorite),
        new SqlMemoryRowCommand(SqlMemoryRowAction.Revisions, SqlIcon.History, "版本歷史", SqlMemoryRowKind.Favorite),
        // 破壞性操作與其他操作隔開，並一律經確認；標籤依列種類說清楚刪的是紀錄還是收藏。
        new SqlMemoryRowCommand(SqlMemoryRowAction.Delete, SqlIcon.Remove, "刪除", SqlMemoryRowKind.Any,
            labelProperty: "DeleteLabel", separated: true, tone: SqlActionTone.Danger),
    };

    public SqlMemoryRowAction Action { get; }
    public SqlIcon Icon { get; }

    /// <summary>靜態標籤；<see cref="LabelProperty"/> 存在時，繫結到列上依狀態變化的說明。</summary>
    public string Label { get; }

    public SqlMemoryRowKind Kind { get; }
    public string? LabelProperty { get; }

    /// <summary>與前一組操作之間留分隔；快捷選單畫分隔線，卡片留較寬的間距。</summary>
    public bool IsSeparated { get; }

    /// <summary>停駐與按下的語意色；只有需要警示或明確歸類的操作離開中性色，其餘沿用選取色。</summary>
    public SqlActionTone Tone { get; }

    /// <summary>這一列的主要動作；窄版只留它，其餘收進 overflow。只有一個，與 Preview 的主要動作同一個。</summary>
    public bool IsPrimary { get; }

    public bool AppliesTo(bool favorite) =>
        Kind == SqlMemoryRowKind.Any || (Kind == SqlMemoryRowKind.Favorite) == favorite;

    public static SqlMemoryRowCommand For(SqlMemoryRowAction action)
    {
        foreach (var command in All) if (command.Action == action) return command;
        throw new ArgumentOutOfRangeException(nameof(action), action, "沒有這個列操作。");
    }
}

/// <summary>History／Favorites 共用的清單；卡片樣板與 Delete 鍵是這份清單自己的，其餘路徑在基底。</summary>
internal sealed class SqlMemoryList : SqlCardListBase<SqlMemoryRowAction>
{
    public SqlMemoryList()
    {
        ItemContainerStyle = SqlAssistChrome.CreateSqlCardStyle();
        ItemTemplate = SqlAssistChrome.CreateSqlSummaryTemplate();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Delete 與檔案總管相同：只對聚焦的卡片發出請求，是否刪除仍由確認框決定。
        if (e.Key == Key.Delete && e.KeyboardDevice.Modifiers == ModifierKeys.None && IsRowContent(e.OriginalSource))
        { e.Handled = true; RequestAction(SqlMemoryRowAction.Delete); }
        base.OnPreviewKeyDown(e);
    }
}
