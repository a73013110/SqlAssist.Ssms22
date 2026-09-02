using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Settings;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.Snippets;
using SqlAssist.Ssms22.Wildcards;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 結構預覽的按鍵操作，以及輸入字元時的關鍵字自動大寫與分隔字元自動配對。
/// </summary>
/// <remarks>
/// 建議清單本身的 Tab／Enter／上下鍵完全由平台處理，這裡不介入——
/// 只有預覽視窗是自己的承載視窗，平台不知道它存在，才需要接手按鍵。
/// 清單沒開著時的 Tab 由 <see cref="SqlTabCommandHandler"/> 接，
/// 它集中處理 Snippet 欄位與萬用字元的優先順序。
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
    ICommandHandler<UpKeyCommandArgs>,
    ICommandHandler<DownKeyCommandArgs>,
    ICommandHandler<PageUpKeyCommandArgs>,
    ICommandHandler<PageDownKeyCommandArgs>,
    ICommandHandler<CopyCommandArgs>,
    ICommandHandler<TypeCharCommandArgs>,
    ICommandHandler<BackspaceKeyCommandArgs>
{
    /// <summary>輸入限定字的點號之後要把建議清單重開一次，那要經過 broker。</summary>
    [Import]
    internal IAsyncCompletionBroker Broker { get; set; } = null!;

    public string DisplayName => "SqlAssist 結構預覽操作";

    public CommandState GetCommandState(EscapeKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(LeftKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(RightKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(UpKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(DownKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(PageUpKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(PageDownKeyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(CopyCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(TypeCharCommandArgs args) => CommandState.Unspecified;

    public CommandState GetCommandState(BackspaceKeyCommandArgs args) => CommandState.Unspecified;

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
        return SqlAssistPlatformGuard.Run(
            "處理 Esc 按鍵",
            () =>
            {
                if (Broker.GetSession(args.TextView) is not null)
                {
                    return false;
                }

                if (SqlStructurePreview.Peek(args.TextView) is { HasSession: false } preview &&
                    preview.Collapse())
                {
                    return true;
                }

                return SqlSnippetExpansionController.Peek(args.TextView)?.EndForEscape() == true;
            },
            fallback: false);
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
        return SqlAssistPlatformGuard.Run(
            "處理 Right 按鍵",
            () =>
            {
                if (SqlAssistSettingsStore.Current.PreviewMode != SqlPreviewMode.RightArrow)
                {
                    return false;
                }

                if (SqlStructurePreview.Peek(args.TextView) is not { } preview)
                {
                    return false;
                }

                var session = Broker.GetSession(args.TextView);
                return preview.RequestExpand(session);
            },
            fallback: false);
    }

    /// <summary>展開狀態下，向左鍵收合；沒展開就照常左移游標。</summary>
    public bool ExecuteCommand(LeftKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return SqlAssistPlatformGuard.Run(
            "處理 Left 按鍵",
            () => SqlStructurePreview.Peek(args.TextView) is { HasSession: true } preview
                && preview.Collapse(),
            fallback: false);
    }

    /// <summary>
    /// 清單方向鍵仍交給平台處理；在平台改選取前，先讓舊預覽目標失效。
    /// </summary>
    /// <remarks>
    /// Completion 的說明 callback 是非同步的。如果不先失效，使用者快速按「下、右」時，
    /// 向右鍵可能會展開上一項。這裡永遠回傳 false，不攔截平台原本的清單導覽。
    /// </remarks>
    public bool ExecuteCommand(UpKeyCommandArgs args, CommandExecutionContext executionContext) =>
        InvalidateCompletionSelection(args.TextView);

    public bool ExecuteCommand(DownKeyCommandArgs args, CommandExecutionContext executionContext) =>
        InvalidateCompletionSelection(args.TextView);

    public bool ExecuteCommand(PageUpKeyCommandArgs args, CommandExecutionContext executionContext) =>
        InvalidateCompletionSelection(args.TextView);

    public bool ExecuteCommand(PageDownKeyCommandArgs args, CommandExecutionContext executionContext) =>
        InvalidateCompletionSelection(args.TextView);

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
        return SqlAssistPlatformGuard.Run(
            "處理 Copy 按鍵",
            () => args.TextView.Selection.IsEmpty
                && SqlStructurePreview.Peek(args.TextView) is { } preview
                && preview.CopySelectionIfAny(),
            fallback: false);
    }

    /// <summary>
    /// 輸入字元時把剛打完的關鍵字轉成大寫、自動配對分隔字元，並在詞元結束時重開建議清單。
    /// </summary>
    /// <remarks>
    /// 只有「跳過自己補上的結尾字元」與「包夾選取範圍」會回傳 true——那兩次
    /// 使用者要的字元已經在文字裡了，再讓編輯器插入一次就是多一個。其餘一律
    /// 回傳 false：改寫大寫與補上結尾字元都只動已經在緩衝區裡的文字，
    /// 使用者輸入的字元仍然交給編輯器插入，其他擴充也還看得到這次按鍵。
    ///
    /// 這裡只用字元本身做第一層篩選，而那條規則與重開清單的判斷共用同一份
    /// （<see cref="SqlCompletionTriggers.MayChangeContext"/>）：篩掉的字元
    /// 連排程都不必，而放行的字元不代表一定會重開。真正的判斷在
    /// <see cref="SqlCompletionReopen.AfterSeparator"/> 裡，
    /// 因為它要看的是這個字元<b>已經進入緩衝區之後</b>的文字：
    /// 此時此刻那個字元還沒被插入。
    /// </remarks>
    public bool ExecuteCommand(TypeCharCommandArgs args, CommandExecutionContext executionContext)
    {
        var handled = SqlAssistPlatformGuard.Run(
            "處理 TypeChar 按鍵",
            () =>
            {
                // 自動大寫與自動配對都是 Snippet Engine 之外的緩衝區編輯，
                // 欄位 session 開著時暫停它們，避免欄位標記或同名欄位同步被外部修改打斷。
                if (SqlSnippetExpansionController.Peek(args.TextView)?.HasActiveSession == true)
                {
                    return false;
                }

                SqlKeywordCasing.ApplyBeforeTypedCharacter(
                    args.TextView,
                    args.SubjectBuffer,
                    args.TypedChar);

                // 建議清單開著時一律讓開：那一次 TypeChar 可能是提交鍵，
                // 吞掉它等於提交不了；而在 session 中途插字元也會讓適用範圍失準。
                if (Broker.GetSession(args.TextView) is not null)
                {
                    return false;
                }

                return SqlAutoPairing.TryHandleTypedCharacter(
                    args.TextView,
                    args.SubjectBuffer,
                    args.TypedChar);
            },
            fallback: false);

        if (SqlCompletionTriggers.MayChangeContext(args.TypedChar))
        {
            SqlAssistPlatformGuard.Run(
                "處理 TypeChar 按鍵",
                () => SqlCompletionReopen.AfterSeparator(args.TextView, Broker));
        }

        return handled;
    }

    /// <summary>
    /// Backspace 刪掉開頭字元時，把自動補上的另一半一起收掉。
    /// </summary>
    /// <remarks>
    /// 參與條件與 TypeChar 完全相同（Snippet 欄位、建議清單各自讓開），
    /// 兩邊分岔的症狀是「補得出來卻收不掉」：打了左括號馬上後悔按 Backspace，
    /// 右括號留在原地。
    /// </remarks>
    public bool ExecuteCommand(BackspaceKeyCommandArgs args, CommandExecutionContext executionContext)
    {
        return SqlAssistPlatformGuard.Run(
            "處理 Backspace 按鍵",
            () => SqlSnippetExpansionController.Peek(args.TextView)?.HasActiveSession != true
                && Broker.GetSession(args.TextView) is null
                && SqlAutoPairing.TryHandleBackspace(args.TextView, args.SubjectBuffer),
            fallback: false);
    }

    /// <summary>只撤銷預覽目標，不吞掉按鍵；平台仍完整執行原本命令。</summary>
    private bool InvalidateCompletionSelection(ITextView textView)
    {
        SqlAssistPlatformGuard.Run(
            "使舊的結構預覽選取失效",
            () =>
            {
                var session = Broker.GetSession(textView);
                SqlStructurePreview.Peek(textView)?.InvalidateSelection(session);
            });

        return false;
    }
}
