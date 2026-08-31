using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 結構預覽用到的平台定義。
/// </summary>
/// <remarks>
/// 這是欄位形式的 MEF 匯出，編輯器以此建立對應的執行個體。
/// 欄位永遠不會被指派，編譯器的 CS0649 在這裡是預期的。
/// </remarks>
internal sealed class SqlPreviewDefinitions
{
    /// <summary>
    /// 預覽視窗所屬的空間保留管理員名稱。
    /// </summary>
    /// <remarks>
    /// 排在內建的 <c>completion</c> 之後，編輯器就會先讓建議清單佔位，再把它保留的
    /// 幾何交給我們——那份幾何是避障的輸入，不是位置本身。落點由
    /// <c>SqlAssist.Core.Preview.PreviewPlacementEngine</c> 算，因為內建的 PopupAgent
    /// 會依 Windows 的左右手功能表設定把畫面翻到它回報矩形的反側。
    /// </remarks>
    public const string SpaceReservationManagerName = "SqlAssistStructurePreview";

#pragma warning disable 649

    [Export]
    [Name(SpaceReservationManagerName)]
    [Order(After = "completion")]
    internal static SpaceReservationManagerDefinition? PreviewSpaceReservationManagerDefinition;

#pragma warning restore 649
}
