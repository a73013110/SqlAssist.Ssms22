using System;
using System.Collections.Generic;

namespace SqlAssist.Ssms22.UI;

/// <summary>版本時間軸的列操作；列上按鈕、快捷選單與右側差異面板都以它為 Tag。</summary>
internal enum SqlFavoriteRevisionAction { Preview, Open, Copy, Revert }

/// <summary>
/// 一個版本操作的外觀；時間軸列、快捷選單與差異面板都從 <see cref="All"/> 建立，順序與色調只有這一份。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlMemoryRowCommand"/> 同一種模式：動線固定為「看 → 帶走 → 會產生新版本的操作」，
/// 呼叫端只能整段跳過不適用的項目，不得自己重排。回溯不刪也不改寫任何版本，但會讓收藏多一筆目前版本，
/// 所以和「新增至收藏」同用收藏色調，而不是破壞性的紅色。
/// </remarks>
internal sealed class SqlFavoriteRevisionCommand
{
    private SqlFavoriteRevisionCommand(SqlFavoriteRevisionAction action, SqlIcon icon, string label,
        bool separated = false, SqlActionTone tone = SqlActionTone.Neutral, bool hiddenOnCurrent = false, string? labelProperty = null)
    {
        Action = action; Icon = icon; Label = label; IsSeparated = separated; Tone = tone;
        HiddenOnCurrent = hiddenOnCurrent; LabelProperty = labelProperty;
    }

    public static IReadOnlyList<SqlFavoriteRevisionCommand> All { get; } = new[]
    {
        new SqlFavoriteRevisionCommand(SqlFavoriteRevisionAction.Preview, SqlIcon.Preview, "預覽此版本全文"),
        new SqlFavoriteRevisionCommand(SqlFavoriteRevisionAction.Open, SqlIcon.Open, "以此版本開新 Query（不執行）"),
        new SqlFavoriteRevisionCommand(SqlFavoriteRevisionAction.Copy, SqlIcon.Copy, "複製此版本 SQL"),
        new SqlFavoriteRevisionCommand(SqlFavoriteRevisionAction.Revert, SqlIcon.Revert, "回溯為新版本",
            separated: true, tone: SqlActionTone.Favorite, hiddenOnCurrent: true, labelProperty: "RevertLabel"),
    };

    public SqlFavoriteRevisionAction Action { get; }
    public SqlIcon Icon { get; }
    public string Label { get; }
    public bool IsSeparated { get; }
    public SqlActionTone Tone { get; }

    /// <summary>目前版本不能回溯成自己；收起而不是停用佔位。</summary>
    public bool HiddenOnCurrent { get; }

    /// <summary>存在時，列上的說明繫結到依狀態變化的文字（例如為什麼不能回溯）。</summary>
    public string? LabelProperty { get; }

    public static SqlFavoriteRevisionCommand For(SqlFavoriteRevisionAction action)
    {
        foreach (var command in All) if (command.Action == action) return command;
        throw new ArgumentOutOfRangeException(nameof(action), action, "沒有這個版本操作。");
    }
}

/// <summary>收藏版本時間軸；鍵盤、續頁與按鈕派送與 History 清單共用基底。</summary>
internal sealed class SqlFavoriteRevisionList : SqlCardListBase<SqlFavoriteRevisionAction>
{
    public SqlFavoriteRevisionList(bool? motion = null)
    {
        ItemContainerStyle = SqlAssistChrome.CreateRevisionItemStyle(motion);
        ItemTemplate = SqlAssistChrome.CreateRevisionItemTemplate();
    }
}
