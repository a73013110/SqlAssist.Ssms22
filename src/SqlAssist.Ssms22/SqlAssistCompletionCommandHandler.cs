using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22.Completion;

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
    ICommandHandler<TypeCharCommandArgs>
{
    public string DisplayName => "SqlAssist 建議清單操作";

    public CommandState GetCommandState(TabKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(ReturnKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(UpKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(DownKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(EscapeKeyCommandArgs args) => CommandState.Unspecified;

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

    public bool ExecuteCommand(EscapeKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return Execute("Esc", () =>
            TryGetController(args.TextView, out var controller) && controller.Hide());
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
