using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SqlAssist.Ssms22.Structure;

/// <summary>
/// 「SqlAssist 物件結構」工具視窗。
/// </summary>
/// <remarks>
/// 選工具視窗而不是提示視窗或另開分頁：它可以釘在編輯器旁邊，
/// 邊寫查詢邊對照欄位，而且捲動與選取都由 WPF 負責，不必再繞剪貼簿。
/// </remarks>
[Guid(WindowGuidString)]
internal sealed class SqlObjectStructureWindow : ToolWindowPane
{
    public const string WindowGuidString = "d1c9a5e2-8f47-4a3b-9c06-2f5b7d84e611";

    public SqlObjectStructureWindow()
        : base(null)
    {
        Caption = "SqlAssist 物件結構";
        Content = new SqlObjectStructureControl();
    }

    /// <summary>面板內容；由 <see cref="SqlObjectStructurePresenter"/> 用來切換顯示的物件。</summary>
    internal SqlObjectStructureControl Control => (SqlObjectStructureControl)Content;
}
