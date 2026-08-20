using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22;

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
    ICommandHandler<EscapeKeyCommandArgs>
{
    public string DisplayName => "SqlAssist 建議清單操作";

    public CommandState GetCommandState(TabKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(ReturnKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(UpKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(DownKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(EscapeKeyCommandArgs args) => CommandState.Unspecified;

    public bool ExecuteCommand(TabKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        SqlAssistRuntimeState.MarkTabReceived("Suggestion CommandHandler");
        return TryGetController(args.TextView, out var controller) && controller.CommitSelected();
    }

    public bool ExecuteCommand(ReturnKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return TryGetController(args.TextView, out var controller) && controller.CommitSelected();
    }

    public bool ExecuteCommand(UpKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return TryGetController(args.TextView, out var controller) && controller.MoveSelection(-1);
    }

    public bool ExecuteCommand(DownKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return TryGetController(args.TextView, out var controller) && controller.MoveSelection(1);
    }

    public bool ExecuteCommand(EscapeKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return TryGetController(args.TextView, out var controller) && controller.Hide();
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
