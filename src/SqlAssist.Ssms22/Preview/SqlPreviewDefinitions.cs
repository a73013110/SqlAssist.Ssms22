using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 結構預覽用到的平台定義：專屬內容類型與空間保留管理員。
/// </summary>
/// <remarks>
/// 這些是欄位形式的 MEF 匯出，編輯器以此建立對應的執行個體。
/// 欄位永遠不會被指派，編譯器的 CS0649 在這裡是預期的。
/// </remarks>
internal sealed class SqlPreviewDefinitions
{
    /// <summary>預覽緩衝區的內容類型。</summary>
    /// <remarks>
    /// 刻意不重用查詢視窗的 <c>SQL</c>：那個內容類型上掛著 SSMS 自己的語言服務，
    /// 也掛著本擴充的建議來源與提示來源。預覽只需要著色，
    /// 用專屬的內容類型才能保證它不會反過來觸發一整套 IntelliSense。
    /// </remarks>
    public const string ContentTypeName = "SqlAssist.StructurePreview";

    /// <summary>
    /// 預覽視窗所屬的空間保留管理員名稱。
    /// </summary>
    /// <remarks>
    /// 排在內建的 <c>completion</c> 之後，編輯器就會先讓建議清單佔位，
    /// 再要求我們的視窗在剩下的空間裡定位——「貼在清單旁邊、
    /// 撞到螢幕邊界就翻到另一側」因此不必自己算，是平台算好的。
    /// </remarks>
    public const string SpaceReservationManagerName = "SqlAssistStructurePreview";

#pragma warning disable 649

    [Export]
    [Name(ContentTypeName)]
    [BaseDefinition("text")]
    internal static ContentTypeDefinition? PreviewContentTypeDefinition;

    [Export]
    [Name(SpaceReservationManagerName)]
    [Order(After = "completion")]
    internal static SpaceReservationManagerDefinition? PreviewSpaceReservationManagerDefinition;

#pragma warning restore 649
}
