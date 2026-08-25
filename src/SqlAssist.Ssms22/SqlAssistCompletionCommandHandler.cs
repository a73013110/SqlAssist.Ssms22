using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Preview;

namespace SqlAssist.Ssms22;

/// <summary>
/// 建議清單的按鍵操作，以及輸入分隔字元時的關鍵字自動大寫。
/// </summary>
/// <remarks>
/// 這些方法都在按鍵路徑上，由編輯器的命令系統直接呼叫。
/// <b>任何一個丟出例外，使用者按一次鍵就會看到一次錯誤對話框</b>，
/// 因此一律收斂例外並回報「沒有處理」，讓按鍵照預設方式往下走。
/// </remarks>
[Export(typeof(ICommandHandler))]
[Name("SqlAssist SSMS 22 Completion Command Handler")]
[Order(Before = "default")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlAssistCompletionCommandHandler :
    ICommandHandler<TabKeyCommandArgs>,
    ICommandHandler<ReturnKeyCommandArgs>,
    ICommandHandler<UpKeyCommandArgs>,
    ICommandHandler<DownKeyCommandArgs>,
    ICommandHandler<EscapeKeyCommandArgs>,
    ICommandHandler<LeftKeyCommandArgs>,
    ICommandHandler<RightKeyCommandArgs>,
    ICommandHandler<TypeCharCommandArgs>
{
    public string DisplayName => "SqlAssist 建議清單操作";

    public CommandState GetCommandState(TabKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(ReturnKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(UpKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(DownKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(EscapeKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(LeftKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(RightKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(TypeCharCommandArgs args) => CommandState.Unspecified;

    public bool ExecuteCommand(TabKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        SqlAssistRuntimeState.MarkTabReceived("Suggestion CommandHandler");
        return Execute("Tab", () =>
            TryGetController(args.TextView, out var controller) && controller.CommitSelected());
    }

    public bool ExecuteCommand(ReturnKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Enter", () =>
            TryGetController(args.TextView, out var controller) && controller.CommitSelected());
    }

    public bool ExecuteCommand(UpKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Up", () =>
            TryGetController(args.TextView, out var controller) && controller.MoveSelection(-1));
    }

    public bool ExecuteCommand(DownKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Down", () =>
            TryGetController(args.TextView, out var controller) && controller.MoveSelection(1));
    }

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
        {
            if (SqlStructurePreview.Peek(args.TextView) is { HasSession: false } preview &&
                preview.Collapse())
            {
                return true;
            }

            return TryGetController(args.TextView, out var controller) && controller.Hide();
        });
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
            if (SettingsService.Default.GetSnapshot().Preview.Mode != SqlPreviewMode.RightArrow)
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

    private static bool TryGetController(ITextView textView, out SqlCompletionController controller)
    {
        if (CompletionSessionRegistry.TryGet(textView, out var found) && found is not null)
        {
            controller = found;
            return true;
        }

        controller = null!;
        return false;
    }
}
