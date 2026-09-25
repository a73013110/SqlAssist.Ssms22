using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Signatures;

/// <summary>
/// 游標停在呼叫的括號裡、參數提示卻不見了的時候，把它請回來。
/// </summary>
/// <remarks>
/// SSMS 的參數資訊只在按鍵打出左括號與逗號時浮出來；刪字、滑鼠點擊、點出去再點回來、
/// 復原，它就不見了，而且不會自己回來。這裡不畫任何東西，只是補送一次
/// <c>Edit.ParameterInfo</c>——內容、涵蓋範圍與開關（SSMS 自己的「參數資訊」核取方塊）
/// 都還是它的。純量函式那一格 SSMS 一律不給，改請 <see cref="SqlSignatureHelp"/>。
///
/// SSMS 那一份雖然是舊版語言服務的 MethodTip，畫面上卻是經過編輯器的轉接層
/// （<c>ShimSignatureHelpController</c>）變成 <see cref="ISignatureHelpBroker"/> 上的
/// session——兩份提示外觀一模一樣就是這個原因。所以「畫面上有沒有提示」問 broker 就答得
/// 出來，而每一份提示浮出來時也都會經過 <see cref="SqlSignatureHelpSource"/>，由那裡
/// 告訴這裡「請過的那一次有結果」（<see cref="NoteShown"/>）。
///
/// 聽的是游標與緩衝區的事件，不是各個按鍵：點擊、方向鍵、Home／End、復原、剪下
/// 走的是不同的命令，事件那一端一次涵蓋。什麼時候該請由
/// <see cref="SqlParameterHintRevival"/> 回答。
///
/// 停手才判斷：按住 Backspace 或一路按方向鍵時，每一次都送命令會讓提示閃個不停，
/// 還會每一次都把整份指令碼複製一份去分析。
/// </remarks>
internal sealed class SqlParameterHintKeeper
{
    /// <summary>
    /// 游標移動之後，最多往回掃多少字元確認它還在同一組括號裡。
    /// </summary>
    /// <remarks>
    /// 游標每動一次都要問，所以只掃左括號到游標那一段；跳得比這還遠（Ctrl+End、
    /// 點到另一頁）就直接當成離開——超過這個長度的引數清單罕見，而當成離開的代價
    /// 只是停手之後多請一次。
    /// </remarks>
    private const int MaximumTrackedArgumentLength = 4096;

    private readonly IWpfTextView _textView;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISignatureHelpBroker? _signatureBroker;
    private readonly DispatcherTimer _idle;

    /// <summary>上一次判斷時游標所在呼叫的左括號；離開過就是 null。</summary>
    /// <remarks>
    /// Negative：在括號前面插字時跟著往後挪，插在括號後面（引數裡）時留在原地——
    /// 與 <see cref="SqlSignatureHelp"/> 記左括號的方式相同。
    /// </remarks>
    private ITrackingPoint? _call;

    /// <summary><see cref="_call"/> 那個呼叫的狀態。</summary>
    private SqlParameterHintCallState _state;

    private SqlParameterHintEdits _edits;
    private bool _escaped;

    private SqlParameterHintKeeper(
        IWpfTextView textView,
        IServiceProvider serviceProvider,
        ISignatureHelpBroker? signatureBroker)
    {
        _textView = textView;
        _serviceProvider = serviceProvider;
        _signatureBroker = signatureBroker;
        _idle = new DispatcherTimer(DispatcherPriority.Background, textView.VisualElement.Dispatcher)
        {
            Interval = SqlAssistChrome.Debounce.ParameterHint,
        };
        _idle.Tick += OnIdle;
    }

    /// <summary>建立編輯器時接上；開關由每一次判斷當場讀，設定之後才打開也照樣生效。</summary>
    public static void Attach(
        IWpfTextView textView,
        IServiceProvider serviceProvider,
        ISignatureHelpBroker? signatureBroker)
    {
        if (textView is null || serviceProvider is null)
        {
            return;
        }

        var keeper = new SqlParameterHintKeeper(textView, serviceProvider, signatureBroker);

        textView.Properties[typeof(SqlParameterHintKeeper)] = keeper;
        textView.TextBuffer.Changed += keeper.OnBufferChanged;
        textView.Caret.PositionChanged += keeper.OnCaretMoved;
        textView.Closed += keeper.OnClosed;
    }

    /// <summary>
    /// 使用者按了 Esc，而且那一次不是拿來收建議清單的。
    /// </summary>
    /// <remarks>
    /// 只記下來，不收任何東西：Esc 本身照常往下走，提示由它的主人收。
    /// </remarks>
    public static void NoteEscape(ITextView textView)
    {
        if (Peek(textView) is { } keeper)
        {
            keeper._escaped = true;
            keeper.Restart();
        }
    }

    /// <summary>
    /// 從別的路徑（提交函式時補上括號）請出提示，並記下這一輪已經請過。
    /// </summary>
    /// <remarks>
    /// 記下來是為了不被請兩次：停手之後這裡再請一次，會把剛浮出來的提示收掉重畫。
    /// 照樣排一次判斷：要記下游標現在所在的呼叫，下一次走出去再走回來才認得出是「走進來」。
    /// </remarks>
    /// <param name="scalarHelp">純量函式那一份；沒有掛上時只請 SSMS 那一份。</param>
    public static void Summon(ITextView textView, SqlSignatureHelp? scalarHelp)
    {
        Request(scalarHelp);

        if (Peek(textView) is { } keeper)
        {
            keeper._edits |= SqlParameterHintEdits.Summoned;
            keeper.Restart();
        }
    }

    /// <summary>
    /// 兩份提示一起請：SSMS 的參數資訊與自己的純量函式簽章。
    /// </summary>
    /// <remarks>
    /// 兩者認得的名稱不重疊（SSMS 一律不給純量函式，這裡只給純量函式），
    /// 不必先判斷是哪一種，浮得出來的只會有一份。
    /// </remarks>
    private static void Request(SqlSignatureHelp? scalarHelp)
    {
        SqlShellParameterInfo.Request();
        scalarHelp?.Request();
    }

    /// <summary>
    /// 有一份提示浮出來了（不論是誰請的）。
    /// </summary>
    /// <remarks>
    /// 呼叫端是平台組 session 的中途，那一刻還不知道這一份最後有沒有內容（沒有內容時
    /// 平台會自己收掉），所以排到這一輪之後再問 broker。
    ///
    /// 不排判斷：提示浮出來不會讓它該被請回來，只是讓這個呼叫之後被收掉時可以再請。
    /// </remarks>
    public static void NoteShown(ITextView textView)
    {
        if (Peek(textView) is not { } keeper)
        {
            return;
        }

        TextViewDispatch.AfterCurrentCommand(textView, "記下參數提示已浮出", _ =>
        {
            if (keeper._signatureBroker?.IsSignatureHelpActive(keeper._textView) == true)
            {
                keeper._state = SqlParameterHintRevival.Shown(keeper._state);
            }
        });
    }

    private static SqlParameterHintKeeper? Peek(ITextView textView) =>
        textView.Properties.TryGetProperty(typeof(SqlParameterHintKeeper), out SqlParameterHintKeeper keeper)
            ? keeper
            : null;

    private static bool IsEnabled
    {
        get
        {
            var settings = SqlAssistSettingsStore.Current;
            return settings.Enabled && settings.ParameterHintEnabled;
        }
    }

    /// <remarks>每按一次鍵都會進來一次；關掉之後只剩一次設定讀取。</remarks>
    private void OnBufferChanged(object sender, TextContentChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("記下參數提示續接的編輯", () =>
        {
            if (!IsEnabled)
            {
                return;
            }

            foreach (var change in args.Changes)
            {
                _edits |= SqlParameterHintRevival.Classify(change.OldLength, change.NewText);
            }

            Restart();
        });

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("記下參數提示續接的游標", () =>
        {
            if (!IsEnabled)
            {
                return;
            }

            // 停手之前就走出那組括號的話，停手時看到的可能又是同一組（出去又回來），
            // 那一次走回來是真的「走進來」，要當場記下離開過。
            if (_call is not null && !IsStillInside(_call, args.NewPosition.BufferPosition))
            {
                _call = null;
                _state = SqlParameterHintCallState.Open;
            }

            Restart();
        });

    /// <remarks>
    /// 走進引數裡的另一個呼叫也算離開（<see cref="SqlCallSignature.TrackArgument"/>
    /// 在那裡回傳 null）：那是另一個函式，回到外層時要當成重新走進來。
    /// </remarks>
    private static bool IsStillInside(ITrackingPoint call, SnapshotPoint caret)
    {
        var snapshot = caret.Snapshot;
        var open = call.GetPosition(snapshot);

        if (open < 0 || open >= snapshot.Length || caret.Position <= open ||
            caret.Position - open > MaximumTrackedArgumentLength)
        {
            return false;
        }

        return SqlCallSignature.TrackArgument(snapshot.GetText(open, caret.Position - open)) is not null;
    }

    private void Restart()
    {
        _idle.Stop();
        _idle.Start();
    }

    private void OnIdle(object sender, EventArgs args)
    {
        _idle.Stop();
        SqlAssistPlatformGuard.Run("判斷要不要請回參數提示", Evaluate);
    }

    private void Evaluate()
    {
        var edits = _edits;
        var escaped = _escaped;
        _edits = SqlParameterHintEdits.None;
        _escaped = false;

        if (_textView.IsClosed || !IsEnabled)
        {
            return;
        }

        var snapshot = _textView.TextBuffer.CurrentSnapshot;
        var caret = _textView.Caret.Position.BufferPosition;

        if (caret.Snapshot != snapshot)
        {
            return;
        }

        var text = snapshot.GetText();
        var position = caret.Position;

        // 分析要掃過游標之前的整份文字，放到背景；回來時文字或游標變了就把這一輪的
        // 編輯還回去，交給已經排上的下一輪——那一輪看到的才是使用者停手的位置。
        SqlAssistPlatformGuard.Begin(
            NotificationCatalog.ShowingSignatureHelp,
            async () =>
            {
                var site = await Task
                    .Run(() => SqlCallSignature.Resolve(text, position, includeKeywordFunctions: true))
                    .ConfigureAwait(false);

                TextViewDispatch.AfterCurrentCommand(_textView, "請回參數提示", _ =>
                    Apply(snapshot, position, site, edits, escaped));
            },
            NotificationKind.Completion, NotificationOrigin.Typing, NotificationLevel.Debug,
            ActiveSqlEditor.GetDocumentName(_textView));
    }

    private void Apply(
        ITextSnapshot snapshot,
        int position,
        SqlCallSignatureContext? site,
        SqlParameterHintEdits edits,
        bool escaped)
    {
        if (_textView.TextBuffer.CurrentSnapshot != snapshot ||
            _textView.Caret.Position.BufferPosition.Position != position)
        {
            _edits |= edits;
            _escaped |= escaped;
            return;
        }

        var visible = _signatureBroker?.IsSignatureHelpActive(_textView) == true;
        var decision = SqlParameterHintRevival.Decide(
            _call?.GetPosition(snapshot),
            _state,
            site?.OpenParenthesis,
            edits,
            escaped,
            visible);

        _call = decision.Call is { } call
            ? snapshot.CreateTrackingPoint(call, PointTrackingMode.Negative)
            : null;
        _state = decision.State;

        // 命令送到目前有焦點的視窗；焦點在預覽或別的工具視窗時送出去就是送錯地方。
        if (decision.Summon && site is not null && _textView.HasAggregateFocus)
        {
            Revive(snapshot, site, edits, visible);
        }
    }

    /// <summary>這個呼叫要不要連自己那一份一起請；不必時為 null。</summary>
    /// <remarks>
    /// 純量函式一定寫結構描述，沒有限定字的名稱不必再查一次中繼資料。
    /// 自己那一份還開著時不重開：它自己跟著編輯走，重開只會閃一下。
    /// </remarks>
    private SqlSignatureHelp? ScalarHelpFor(SqlCallSignatureContext site)
    {
        if (!site.IsQualified || _signatureBroker is not { } broker)
        {
            return null;
        }

        var help = SqlCompletionServices.GetSignatureHelp(_textView, _serviceProvider, broker);
        return help.IsActive ? null : help;
    }

    private void Revive(ITextSnapshot snapshot, SqlCallSignatureContext site, SqlParameterHintEdits edits, bool visible)
    {
        Request(ScalarHelpFor(site));

        SqlAssistDiagnostics.Write(
            $"請回參數提示：{snapshot.GetText(site.NameStart, site.NameEnd - site.NameStart)}（編輯 {edits}，提示原本{(visible ? "開著" : "沒開")}）",
            _textView);
    }

    private void OnClosed(object sender, EventArgs args)
    {
        _textView.TextBuffer.Changed -= OnBufferChanged;
        _textView.Caret.PositionChanged -= OnCaretMoved;
        _textView.Closed -= OnClosed;
        _idle.Stop();
        _idle.Tick -= OnIdle;
    }
}
