using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Snippets;

namespace SqlAssist.Ssms22.Wildcards;

/// <summary>
/// Tab／Shift+Tab／Enter 的單一優先順序入口。
/// </summary>
/// <remarks>
/// Tab 在編輯器裡原本有三種意思——提交建議清單、插入定位字元、把選取的幾行縮排。
/// Completion 先取得 Tab；其次是原生 Snippet 欄位；最後才是萬用字元。
/// 優先順序集中在同一個型別，避免兩個 Before=default 的 Tab handler 互相競速。
///
/// <b>清單開著時一定要讓開。</b>本處理常式與平台的建議清單處理常式都排在
/// <c>default</c> 之前，彼此的先後順序沒有保證；不先問過 broker 的話，
/// 使用者按 Tab 想提交清單選取項時可能會變成展開萬用字元。
///
/// 這個方法在按鍵路徑上，丟出例外就是使用者按一次鍵看到一次錯誤對話框，
/// 因此一律收斂並回報沒有處理。
/// </remarks>
[Export(typeof(ICommandHandler))]
[Name("SqlAssist SSMS 22 Tab Command Handler")]
[Order(Before = "default")]
[ContentType("SQL")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class SqlTabCommandHandler :
    ICommandHandler<TabKeyCommandArgs>,
    ICommandHandler<BackTabKeyCommandArgs>,
    ICommandHandler<ReturnKeyCommandArgs>
{
    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    /// <remarks>
    /// 允許缺席：拿不到 broker 時仍可導覽已存在的 Snippet session，
    /// 但不嘗試萬用字元展開，避免在未知的 Completion 狀態下搶走 Tab。
    /// </remarks>
    [Import(AllowDefault = true)]
    internal IAsyncCompletionBroker? Broker { get; set; }

    public string DisplayName => "SqlAssist Tab 與 Snippet 導航";

    public CommandState GetCommandState(TabKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(BackTabKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(ReturnKeyCommandArgs args) => CommandState.Unspecified;

    public bool ExecuteCommand(TabKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return SqlAssistPlatformGuard.Run(
            "處理 Tab 按鍵",
            () =>
            {
                if (Broker?.GetSession(args.TextView) is not null)
                {
                    return false;
                }

                if (SqlSnippetExpansionController.Peek(args.TextView)?.MoveNext() == true)
                {
                    return true;
                }

                if (Broker is null)
                {
                    return false;
                }

                return SqlCompletionServices
                    .GetWildcardExpander(args.TextView, ServiceProvider)
                    .TryExpand();
            },
            fallback: false);
    }

    public bool ExecuteCommand(BackTabKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return SqlAssistPlatformGuard.Run(
            "處理 Shift+Tab 按鍵",
            () => Broker?.GetSession(args.TextView) is null &&
                  SqlSnippetExpansionController.Peek(args.TextView)?.MovePrevious() == true,
            fallback: false);
    }

    public bool ExecuteCommand(ReturnKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        SqlAssistPlatformGuard.Run(
            "處理 Enter 按鍵",
            () =>
            {
                if (Broker?.GetSession(args.TextView) is null)
                {
                    _ = SqlSnippetExpansionController.Peek(args.TextView)?.EndForEnter();
                }
            });

        // Enter 永遠維持換行語意；結束 session 後仍交給預設處理常式。
        return false;
    }
}
