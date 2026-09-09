using System;
using System.Windows.Input;
using Microsoft.VisualStudio;

namespace SqlAssist.Ssms22.Snippets;

/// <summary>
/// 殼層命令換回按鍵與 WPF 命令的對照表。
/// </summary>
/// <remarks>
/// 清單開著的時候，殼層仍照「文字編輯器」範圍把有繫結的鍵解析成命令送進查詢視窗的
/// 命令鏈（見 <see cref="SqlSnippetSurroundPicker"/>）。這裡只負責「那個命令原本是
/// 哪一個鍵」，行為交還給 WPF：修飾鍵是實體狀態，所以 <c>*_EXT</c>（Shift 延伸選取）
/// 與 <c>WORDPREV</c>／<c>WORDNEXT</c>（Ctrl+←／→）都對回同一個方向鍵就好。
///
/// 對照不到的命令不攔——多攔一個就是讓某個鍵在清單開著時安靜地失效。
/// </remarks>
internal static class SqlSnippetSurroundKeys
{
    /// <summary>剪貼簿與復原這幾個直接對得到 WPF 命令，不必繞回按鍵。</summary>
    public static RoutedUICommand? MapCommand(Guid group, uint commandId)
    {
        if (group != VSConstants.GUID_VSStandardCommandSet97)
        {
            return null;
        }

        return (VSConstants.VSStd97CmdID)commandId switch
        {
            VSConstants.VSStd97CmdID.Copy => ApplicationCommands.Copy,
            VSConstants.VSStd97CmdID.Cut => ApplicationCommands.Cut,
            VSConstants.VSStd97CmdID.Paste => ApplicationCommands.Paste,
            VSConstants.VSStd97CmdID.SelectAll => ApplicationCommands.SelectAll,
            VSConstants.VSStd97CmdID.Undo => ApplicationCommands.Undo,
            VSConstants.VSStd97CmdID.Redo => ApplicationCommands.Redo,
            _ => null
        };
    }

    public static Key? MapKey(Guid group, uint commandId)
    {
        if (group == VSConstants.VSStd2K)
        {
            return MapEditorKey((VSConstants.VSStd2KCmdID)commandId);
        }

        if (group == VSConstants.GUID_VSStandardCommandSet97)
        {
            return (VSConstants.VSStd97CmdID)commandId == VSConstants.VSStd97CmdID.Delete ? Key.Delete : null;
        }

        return null;
    }

    private static Key? MapEditorKey(VSConstants.VSStd2KCmdID command) => command switch
    {
        VSConstants.VSStd2KCmdID.UP or VSConstants.VSStd2KCmdID.UP_EXT or
            VSConstants.VSStd2KCmdID.UP_EXT_COL => Key.Up,
        VSConstants.VSStd2KCmdID.DOWN or VSConstants.VSStd2KCmdID.DOWN_EXT or
            VSConstants.VSStd2KCmdID.DOWN_EXT_COL => Key.Down,
        VSConstants.VSStd2KCmdID.LEFT or VSConstants.VSStd2KCmdID.LEFT_EXT or
            VSConstants.VSStd2KCmdID.LEFT_EXT_COL or VSConstants.VSStd2KCmdID.WORDPREV or
            VSConstants.VSStd2KCmdID.WORDPREV_EXT or VSConstants.VSStd2KCmdID.WORDPREV_EXT_COL => Key.Left,
        VSConstants.VSStd2KCmdID.RIGHT or VSConstants.VSStd2KCmdID.RIGHT_EXT or
            VSConstants.VSStd2KCmdID.RIGHT_EXT_COL or VSConstants.VSStd2KCmdID.WORDNEXT or
            VSConstants.VSStd2KCmdID.WORDNEXT_EXT or VSConstants.VSStd2KCmdID.WORDNEXT_EXT_COL => Key.Right,
        VSConstants.VSStd2KCmdID.BOL or VSConstants.VSStd2KCmdID.BOL_EXT or
            VSConstants.VSStd2KCmdID.BOL_EXT_COL or VSConstants.VSStd2KCmdID.HOME or
            VSConstants.VSStd2KCmdID.HOME_EXT => Key.Home,
        VSConstants.VSStd2KCmdID.EOL or VSConstants.VSStd2KCmdID.EOL_EXT or
            VSConstants.VSStd2KCmdID.EOL_EXT_COL or VSConstants.VSStd2KCmdID.END or
            VSConstants.VSStd2KCmdID.END_EXT => Key.End,
        VSConstants.VSStd2KCmdID.PAGEUP or VSConstants.VSStd2KCmdID.PAGEUP_EXT => Key.PageUp,
        VSConstants.VSStd2KCmdID.PAGEDN or VSConstants.VSStd2KCmdID.PAGEDN_EXT => Key.PageDown,

        // Shift+Tab 進來的是 BACKTAB，但 Shift 實體上仍按著，交給 WPF 判斷方向。
        VSConstants.VSStd2KCmdID.TAB or VSConstants.VSStd2KCmdID.BACKTAB => Key.Tab,
        VSConstants.VSStd2KCmdID.RETURN => Key.Enter,
        VSConstants.VSStd2KCmdID.CANCEL => Key.Escape,
        VSConstants.VSStd2KCmdID.BACKSPACE or VSConstants.VSStd2KCmdID.DELETEWORDLEFT => Key.Back,
        VSConstants.VSStd2KCmdID.DELETE or VSConstants.VSStd2KCmdID.DELETEWORDRIGHT => Key.Delete,
        _ => null
    };
}
