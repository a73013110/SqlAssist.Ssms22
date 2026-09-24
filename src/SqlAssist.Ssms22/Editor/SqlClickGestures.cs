using System.Windows.Input;

namespace SqlAssist.Ssms22.Editor;

/// <summary>在物件名稱上點擊時要做的事。</summary>
internal enum SqlClickAction
{
    None,

    /// <summary>浮動結構預覽，等於在那裡按 Ctrl+F12。</summary>
    ShowStructure,

    /// <summary>把定義開進新的查詢視窗，等於在那裡按 F12。</summary>
    GoToDefinition
}

/// <summary>
/// 修飾鍵對應到哪一個點擊動作。新增一種手勢只要在 <see cref="Gestures"/> 加一列，
/// 再在 <c>SqlObjectNavigation.Run</c> 接上動作（測試專案只連結這個檔案，不寫 cref）。
/// </summary>
/// <remarks>
/// 修飾鍵一律<b>完全相同</b>才算：Ctrl+Alt＋點擊是編輯器的多重游標，
/// 「包含 Ctrl 就算」的那一版會把它搶走。
/// Alt 開頭的組合不拿來用：Alt＋拖曳是方塊選取，Ctrl+Alt＋點擊是多重游標。
/// </remarks>
internal static class SqlClickGestures
{
    private static readonly (ModifierKeys Modifiers, SqlClickAction Action)[] Gestures =
    {
        (ModifierKeys.Control, SqlClickAction.ShowStructure),
        (ModifierKeys.Control | ModifierKeys.Shift, SqlClickAction.GoToDefinition)
    };

    public static SqlClickAction Resolve(ModifierKeys modifiers)
    {
        foreach (var gesture in Gestures)
        {
            if (gesture.Modifiers == modifiers) return gesture.Action;
        }

        return SqlClickAction.None;
    }

    /// <summary>這個動作答得出內建名稱嗎；決定 CONVERT 這類名稱要不要加底線。</summary>
    public static bool AcceptsBuiltIns(SqlClickAction action) => action == SqlClickAction.ShowStructure;
}
