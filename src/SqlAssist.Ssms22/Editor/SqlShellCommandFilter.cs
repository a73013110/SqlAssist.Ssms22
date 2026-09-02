using System;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using SqlAssist.Ssms22.Commands;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Settings;

// Microsoft.VisualStudio.OLE.Interop 自己有一個 IServiceProvider（COM 的那個）與
// 一個 Constants，跟殼層的同名。這裡要的都是另一邊，明確指名才不會編譯失敗。
using IServiceProvider = System.IServiceProvider;
using OleConstants = Microsoft.VisualStudio.OLE.Interop.Constants;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 掛在查詢視窗命令鏈最前面的殼層命令濾鏡。
/// </summary>
/// <remarks>
/// <b>為什麼需要它。</b><c>AddCommandFilter</c> 把新濾鏡插在鏈的<b>最前面</b>，
/// 所以這裡看得到的是別人處理之前的命令。現代編輯器的 <c>ICommandHandler</c> 則
/// 只看得到「核心編輯器決定轉進現代管線」的命令，而 SSMS 的查詢視窗在核心編輯器
/// 外面還有自己的文件檢視與舊版語言服務——實測掛在那一族的 F12 處理常式，
/// 建立了、MEF 也沒過期，紀錄檔卻連一行都沒有。這裡是唯一保證攔得到殼層命令的位置。
///
/// <b>目前接到的命令與實際狀況。</b>實測 SSMS 22 並沒有把 F12 綁在
/// <c>Edit.GoToDefinition</c> 上，F12 走的是命令表的全域鍵繫結（見 <c>Menus.vsct</c>），
/// 不經過這裡。<c>GotoDefn</c> 這一支留著是為了讓使用者能在「選項 → 鍵盤」把
/// <c>Edit.GoToDefinition</c> 綁到自己習慣的鍵，成本是每次 <c>QueryStatus</c>
/// 一次 GUID 比對。
///
/// 因此這個類別現在最主要的價值是<b>診斷</b>：它是「按了某個鍵卻沒反應」時，
/// 唯一看得到那一刻送出什麼命令的地方——上面那個結論就是這樣測出來的。
///
/// <b>這是編輯器最熱的路徑。</b><c>QueryStatus</c> 在每一次按鍵、每一次閒置與每一次
/// 開選單時都會被呼叫數十次，<c>Exec</c> 則是每打一個字元一次。因此這裡的規矩是：
/// 先比命令群組 GUID，不相符立刻原封轉給下一個目標，中間不配置任何物件、不取設定
/// 以外的任何服務、不記錄任何東西。多做一件事就是每個按鍵多付一次。
/// </remarks>
internal sealed class SqlShellCommandFilter : IOleCommandTarget
{
    /// <summary>殼層標準命令集，<c>Edit.GoToDefinition</c> 屬於這一組。</summary>
    private static readonly Guid StandardCommandSet = VSConstants.GUID_VSStandardCommandSet97;

    private const uint GoToDefinitionCommandId = (uint)VSConstants.VSStd97CmdID.GotoDefn;

    private const int NotSupported = (int)OleConstants.OLECMDERR_E_NOTSUPPORTED;

    private readonly IWpfTextView _textView;
    private readonly IServiceProvider _serviceProvider;
    private readonly IVsTextView _viewAdapter;

    /// <summary>
    /// 已經記錄過的命令，只在詳細診斷打開時使用。
    /// </summary>
    /// <remarks>
    /// 每個命令只記第一次。全部都記的話打字時的 <c>TYPECHAR</c> 會把紀錄檔灌爆，
    /// 而完全不記就沒有辦法回答「按下那個鍵到底送出了什麼命令」——那正是
    /// 「按了沒反應」這一類問題唯一需要的資訊。
    ///
    /// 跨編輯器共用一份，不是每個編輯器一份：後者會在每開一個查詢視窗時把同一批
    /// 命令重記一輪，而 F12 本身就會開新視窗——診斷的那幾行會被自己製造的噪音蓋掉。
    /// 要問的是「這個鍵送出什麼命令」，那與哪一個編輯器無關。
    ///
    /// 只在 UI 執行緒上存取（命令派送本來就只發生在那裡），不必同步。
    /// </remarks>
    private static readonly HashSet<long> SeenCommands = new();

    private IOleCommandTarget? _next;
    private bool _detached;

    private SqlShellCommandFilter(
        IWpfTextView textView,
        IVsTextView viewAdapter,
        IServiceProvider serviceProvider)
    {
        _textView = textView;
        _viewAdapter = viewAdapter;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 掛上濾鏡；取不到介面卡時安靜地不掛。
    /// </summary>
    /// <remarks>
    /// 掛不上只代表少了一條入口，其餘功能照常——為了它讓整個編輯器初始化失敗
    /// 不值得。真正沒有入口的情況由 <c>SqlAssistCommands</c> 的選單命令與
    /// 鍵繫結兜底。
    /// </remarks>
    public static void Attach(
        IWpfTextView textView,
        IVsEditorAdaptersFactoryService? adapters,
        IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (textView is null || adapters is null)
        {
            return;
        }

        if (adapters.GetViewAdapter(textView) is not { } viewAdapter)
        {
            SqlAssistDiagnostics.WriteAlways("取不到 IVsTextView，殼層命令濾鏡未掛上");
            return;
        }

        var filter = new SqlShellCommandFilter(textView, viewAdapter, serviceProvider);

        if (ErrorHandler.Failed(viewAdapter.AddCommandFilter(filter, out var next)))
        {
            SqlAssistDiagnostics.WriteAlways("AddCommandFilter 失敗，殼層命令濾鏡未掛上");
            return;
        }

        filter._next = next;
        textView.Closed += filter.OnTextViewClosed;

        // 不帶編輯器細節：那一串 ContentType 與 Roles 每個查詢視窗都一樣，
        // 對這一行沒有識別力，只會把診斷用的幾行擠掉。
        SqlAssistDiagnostics.Write("殼層命令濾鏡已掛上");
    }

    /// <remarks>
    /// <b>回報 supported 加 enabled 是這裡的關鍵動作，不是形式。</b>
    /// 殼層在派送 <c>Exec</c> 之前會先問過整條鏈；沒有任何一個目標認領時，
    /// 這個命令就是停用的，而停用的命令連 <c>Exec</c> 都不會發出去——
    /// 症狀正好就是「按 F12 完全沒反應，紀錄檔也什麼都沒有」。
    ///
    /// 只認 <c>cCmds == 1</c>：殼層批次詢問多個命令時陣列裡混著別人的命令，
    /// 整批接下來會把那些一起回報成可用。
    /// </remarks>
    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        if (pguidCmdGroup == StandardCommandSet &&
            cCmds == 1 &&
            prgCmds[0].cmdID == GoToDefinitionCommandId &&
            SqlAssistSettingsStore.Current.Enabled)
        {
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            return VSConstants.S_OK;
        }

        return _next is { } next
            ? next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText)
            : NotSupported;
    }

    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        if (pguidCmdGroup == StandardCommandSet && nCmdID == GoToDefinitionCommandId)
        {
            // 這是按鍵路徑，丟出例外就是使用者按一次鍵看到一次錯誤對話框。
            // 沒有接手時往下轉，讓 SSMS 仍有機會處理。
            if (SqlAssistPlatformGuard.Run("處理移至定義命令", TryGoToDefinition, fallback: false))
            {
                return VSConstants.S_OK;
            }
        }
        else if (SqlAssistSettingsStore.Current.VerboseLogging)
        {
            RecordUnhandled(pguidCmdGroup, nCmdID);
        }

        return _next is { } next
            ? next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut)
            : NotSupported;
    }

    private bool TryGoToDefinition()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SqlAssistDiagnostics.Write("移至定義命令抵達 SqlAssist（殼層命令濾鏡）");

        return SqlCompletionServices
            .GetDefinitionOpener(_textView, _serviceProvider)
            .TryBegin(_textView.Caret.Position.BufferPosition);
    }

    /// <summary>
    /// 記下第一次看到的每一個命令。
    /// </summary>
    /// <remarks>
    /// 「按了某個鍵卻沒反應」的問題，唯一需要的資訊就是那一刻送出了哪個命令。
    /// 有了這一行，下一次同類問題不必再猜命令鏈——按下去之後紀錄檔多出哪一行，
    /// 要攔的就是它。
    ///
    /// SqlAssist 自己命令集裡的命令不記：它們由命令表處理，本來就不該由濾鏡接手，
    /// 記成「未處理」只會讓下一個看紀錄檔的人以為它們掉了。實測 F12 就是走那一條
    /// 進來的，而它在這裡留下的是 <c>{命令集}/522</c> 這種看起來像故障的行。
    /// </remarks>
    private static void RecordUnhandled(Guid group, uint commandId)
    {
        if (group == CommandIds.CommandSet)
        {
            return;
        }

        // 命令群組的雜湊與識別碼併成一個鍵；不同群組撞號的機率可以忽略，
        // 而換成字串鍵就要在每一個按鍵上配置一個字串。
        if (!SeenCommands.Add(((long)group.GetHashCode() << 32) | commandId))
        {
            return;
        }

        SqlAssistDiagnostics.WriteAlways($"未處理的殼層命令：{Describe(group, commandId)}");
    }

    private static string Describe(Guid group, uint commandId)
    {
        if (group == StandardCommandSet)
        {
            return $"VSStd97/{Name<VSConstants.VSStd97CmdID>(commandId)}";
        }

        if (group == VSConstants.VSStd2K)
        {
            return $"VSStd2K/{Name<VSConstants.VSStd2KCmdID>(commandId)}";
        }

        return $"{group:B}/{commandId}";
    }

    private static string Name<TCommandId>(uint commandId) where TCommandId : struct, Enum
    {
        var value = (TCommandId)Enum.ToObject(typeof(TCommandId), commandId);
        return Enum.IsDefined(typeof(TCommandId), value) ? $"{value}({commandId})" : commandId.ToString();
    }

    private void OnTextViewClosed(object sender, EventArgs eventArgs)
    {
        // 明確拆掉：殼層雖然會在銷毀時清掉整條鏈，但濾鏡握著編輯器與服務提供者，
        // 留著就是一份跟著關閉的查詢視窗一起活下去的參考。
        SqlAssistPlatformGuard.Run("移除殼層命令濾鏡", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _textView.Closed -= OnTextViewClosed;

            if (_detached)
            {
                return;
            }

            _detached = true;
            _viewAdapter.RemoveCommandFilter(this);
            _next = null;
        });
    }
}
