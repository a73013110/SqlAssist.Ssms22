using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22;

/// <summary>
/// 結構預覽的按鍵操作，以及輸入分隔字元時的關鍵字自動大寫。
/// </summary>
/// <remarks>
/// 建議清單本身的 Tab／Enter／上下鍵完全由平台處理，這裡不介入——
/// 只有預覽視窗是自己的承載視窗，平台不知道它存在，才需要接手按鍵。
///
/// 這些方法都在按鍵路徑上，由編輯器的命令系統直接呼叫。
/// <b>任何一個丟出例外，使用者按一次鍵就會看到一次錯誤對話框</b>，
/// 因此一律收斂例外並回報「沒有處理」，讓按鍵照預設方式往下走。
/// </remarks>
[Export(typeof(ICommandHandler))]
[Name("SqlAssist SSMS 22 Preview Command Handler")]
[Order(Before = "default")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAssistCompletionCommandHandler :
    ICommandHandler<EscapeKeyCommandArgs>,
    ICommandHandler<LeftKeyCommandArgs>,
    ICommandHandler<RightKeyCommandArgs>,
    ICommandHandler<CopyCommandArgs>,
    ICommandHandler<TypeCharCommandArgs>
{
    public string DisplayName => "SqlAssist 結構預覽操作";

    public CommandState GetCommandState(EscapeKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(LeftKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(RightKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(CopyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(TypeCharCommandArgs args) => CommandState.Unspecified;

    /// <summary>
    /// Esc 收掉預覽。
    /// </summary>
    /// <remarks>
    /// 只處理「不是建議清單開出來的」那種預覽——由清單開出來的，
    /// 讓平台照常關清單就好，清單一關預覽自己會跟著收。
    /// 這樣 Esc 永遠只需要按一次。
    /// </remarks>
    public bool ExecuteCommand(EscapeKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Esc", () =>
            SqlStructurePreview.Peek(args.TextView) is { HasSession: false } preview
            && preview.Collapse());
    }

    /// <summary>
    /// 建議清單開著時，向右鍵展開結構預覽。
    /// </summary>
    /// <remarks>
    /// 只在清單開著、設定是向右鍵模式、而且預覽還沒展開時才吞掉這次按鍵；
    /// 其餘情況一律讓游標照常右移，不改變任何既有的編輯行為。
    /// </remarks>
    public bool ExecuteCommand(RightKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Right", () =>
        {
            if (SqlAssistSettingsStore.Current.PreviewMode != SqlPreviewMode.RightArrow)
            {
                return false;
            }

            return SqlStructurePreview.Peek(args.TextView) is { HasSession: true } preview
                && preview.Expand();
        });
    }

    /// <summary>展開狀態下，向左鍵收合；沒展開就照常左移游標。</summary>
    public bool ExecuteCommand(LeftKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Left", () =>
            SqlStructurePreview.Peek(args.TextView) is { HasSession: true } preview
            && preview.Collapse());
    }

    /// <summary>
    /// 預覽視窗裡有選取時，Ctrl+C 複製的是它。
    /// </summary>
    /// <remarks>
    /// 浮動預覽是自己的一個承載視窗，拿不到鍵盤焦點，所以 Ctrl+C 會落在查詢視窗的
    /// 命令鏈上——使用者明明在預覽裡拉好了選取，按下去卻什麼也沒發生。
    /// 這裡把命令轉過去，但只在<b>編輯器自己沒有選取</b>時才接手：
    /// 他在查詢視窗裡選了字要複製，那當然是他的優先。
    /// </remarks>
    public bool ExecuteCommand(CopyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Copy", () =>
            args.TextView.Selection.IsEmpty
            && SqlStructurePreview.Peek(args.TextView) is { } preview
            && preview.CopySelectionIfAny());
    }

    /// <summary>
    /// 輸入字元時把剛打完的關鍵字轉成大寫。
    /// </summary>
    /// <remarks>
    /// 一律回傳 false：這個處理常式只負責改寫已經在緩衝區裡的那個字，
    /// 使用者輸入的字元仍然交給編輯器插入，其他擴充也還看得到這次按鍵。
    /// </remarks>
    public bool ExecuteCommand(TypeCharCommandArgs args, CommandExecutionContext executionContext)
    {
        Execute("TypeChar", () =>
        {
            SqlKeywordCasing.ApplyBeforeTypedCharacter(args.TextView, args.SubjectBuffer, args.TypedChar);
            return false;
        });

        return false;
    }

    private static bool Execute(string source, Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"處理 {source} 按鍵失敗：{exception}");
            return false;
        }
    }
}
