using System;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22.Editor;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Preview;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 編輯器上的浮動結構預覽。
/// </summary>
/// <remarks>
/// 仍掛在編輯器的空間保留機制上，讓預覽焦點算進編輯器的聚合焦點；
/// 實際位置則由自訂 Agent 明確計算，避免平台因 Windows 左右手設定把畫面翻到回報矩形的反側。
///
/// 兩件事分開管：建議清單目前選到誰（<see cref="_selection"/>，只是記帳），與畫面上正在
/// 顯示誰、它活多久（<see cref="_subject"/> 與 <see cref="_mode"/>）。什麼時候收起來只問
/// <see cref="PreviewLifecycle"/>，這裡只負責把平台事件翻成它的訊號。
/// </remarks>
internal sealed class SqlStructurePreview
{
    /// <summary>
    /// 展開狀態下換選取時，多久之後才真的去查資料庫。
    /// </summary>
    /// <remarks>
    /// 用方向鍵連續移動時，每一格都送出一次查詢是純浪費——停下來的那一格才是
    /// 使用者要看的。這是實作細節而不是偏好，所以不開放設定。
    /// </remarks>
    private const int QueryDebounceMilliseconds = 150;

    /// <summary>
    /// 自動展開的最短延遲。
    /// </summary>
    /// <remarks>
    /// 設定允許 0，但 0 表示「按鍵一到就展開」，那在方向鍵連按時等於每一格
    /// 都重畫一次版面。留一格最小緩衝，讓連按仍然掃得過去。
    /// </remarks>
    private const int MinimumExpandDelayMilliseconds = 50;

    private readonly IWpfTextView _view;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>停夠久自動展開的倒數。</summary>
    /// <remarks>
    /// 與查詢節流分開兩個計時器：清單開著時指名打開的預覽可能還在等查詢，
    /// 共用一個的話在清單上換一次選取就會把那個查詢停掉。
    /// </remarks>
    private readonly DispatcherTimer _expandTimer;

    /// <summary>畫面上的物件換定之後才真的去查資料庫的節流。</summary>
    private readonly DispatcherTimer _queryTimer;

    private SqlStructurePreviewControl? _control;
    private ISpaceReservationManager? _manager;
    private SqlPreviewPopupAgent? _agent;
    private ITrackingSpan? _anchor;
    private IAsyncCompletionSession? _observedSession;
    private IAsyncCompletionSession? _session;

    /// <summary>
    /// 預覽由誰打開，也就決定它活多久；規則見 <see cref="PreviewLifecycle"/>。
    /// </summary>
    /// <remarks>
    /// 與「視窗在不在畫面上」分開：清單上展開之後選到沒有結構的項目（關鍵字、片段、讀不出
    /// 資料行的宣告）時只收掉視窗，<see cref="PreviewMode.Browse"/> 留著，移回有結構的項目
    /// 就自己出現。合成同一個狀態的話只剩兩種壞選擇——畫一個寫著「沒有結構」的空視窗擋在
    /// 清單旁邊，或整個收合，讓使用者每路過一個關鍵字就得再按一次向右鍵。
    /// </remarks>
    private PreviewMode _mode;

    /// <summary>顯示時錨點上的文字；指名打開的預覽靠它認出「那個名稱被改了」。</summary>
    private string? _anchorText;

    /// <summary>畫面上的預覽錨在哪裡；滑鼠停留提示在背景執行緒上讀，所以單獨一格。</summary>
    private volatile ITrackingSpan? _shownAnchor;

    /// <summary>本輪命令結束後要重新判斷一次錨點；同一次按鍵的游標與文字事件併成一次。</summary>
    private bool _anchorCheckQueued;

    /// <summary>
    /// 建議清單目前選到的項目：對帳驗證過的主體，與它所屬的中繼資料服務。
    /// </summary>
    /// <remarks>
    /// 只是記帳，不代表畫面：清單開著時畫面上可能是指名打開或釘住的另一個物件。
    /// 向右鍵與停夠久展開拿的是這一份。
    /// </remarks>
    private SqlPreviewSubject? _selection;

    private SqlMetadataService? _selectionService;

    /// <summary>
    /// 畫面上正在顯示的東西：一個資料庫物件，或一份內建名稱的說明。
    /// </summary>
    /// <remarks>
    /// 兩種內容共用同一格而不是各佔一個欄位，理由見 <see cref="SqlPreviewSubject"/>。
    /// 指令碼自己宣告的暫存資料表、資料表變數與 CTE 已經讀好的結構也掛在它身上：
    /// 它們的 <c>object_id</c> 一律是 0，中繼資料的第二、三層快取卻是照編號存的——
    /// 交給一般的載入路徑不是拿到別的東西，就是白等一次查不到東西的查詢。
    /// </remarks>
    private SqlPreviewSubject? _subject;

    /// <summary>目前這份文字的指令碼宣告名冊，與它所屬的版本。</summary>
    /// <remarks>
    /// 名冊要掃過整份文字，所以只在真的要畫的時候才建，而且照版本留著：使用者
    /// 按著方向鍵在清單裡上下走時文字一個字都沒動，那一段路上一次都不必重掃。
    /// 反過來，文字一改就得換一份——舊的那一份會交出使用者已經刪掉的宣告。
    /// </remarks>
    private ITextSnapshot? _declarationsSnapshot;

    private SqlScriptDeclarations? _declarations;

    private SqlMetadataService? _metadataService;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _selectionRefresh;
    private bool _closed;

    private bool _layoutUpdateQueued;

    /// <summary>對帳確認過目前這一項畫得出東西；向右鍵靠它決定要不要吞掉按鍵。</summary>
    private bool _selectedItemHasContent;

    private bool _selectionPending;

    /// <summary>選取尚在背景對帳時收到向右鍵，驗證成功後替使用者完成展開。</summary>
    private bool _expandWhenSelectionReady;

    private bool _inputTrackingAttached;

    /// <summary>已經排了一次預先建立；建議清單每開一次都會呼叫 <see cref="Warmup"/>。</summary>
    private bool _warmupQueued;

    /// <summary>畫面換內容或收起時遞增；過期的查詢與節流不得越代更新。</summary>
    private long _generation;

    /// <summary>清單換 session 或選取時遞增；過期的對帳與自動展開倒數不得越代套用。</summary>
    private long _selectionGeneration;

    private long _queryGeneration;

    private long _expandGeneration;

    private double _resizeStartWidth;

    private double _resizeStartHeight;

    /// <summary>拖曳開始時這一軸就已經是版面壓縮的結果；壓縮值不得寫回偏好尺寸。</summary>
    private bool _resizeStartWidthConstrained;

    private bool _resizeStartHeightConstrained;

    private SqlStructurePreview(IWpfTextView view, IServiceProvider serviceProvider)
    {
        _view = view;
        _serviceProvider = serviceProvider;

        var dispatcher = view.VisualElement.Dispatcher;
        _expandTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _expandTimer.Tick += OnExpandTimerTick;
        _queryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(QueryDebounceMilliseconds)
        };
        _queryTimer.Tick += OnQueryTimerTick;

        view.Closed += OnViewClosed;
        view.LayoutChanged += OnViewLayoutChanged;
        view.ViewportLeftChanged += OnViewportGeometryChanged;
        view.ViewportWidthChanged += OnViewportGeometryChanged;
        view.ViewportHeightChanged += OnViewportGeometryChanged;
        view.ZoomLevelChanged += OnZoomLevelChanged;
        view.Caret.PositionChanged += OnCaretPositionChanged;
        view.TextBuffer.Changed += OnTextBufferChanged;
        SqlLanguageSwitch.Changed += OnLanguageChanged;
    }

    private static SqlPreviewPlacement Placement =>
        SqlAssistSettingsStore.Current.PreviewPlacement;

    /// <summary>
    /// 預覽正跟著建議清單的選取換內容；那時清單旁的說明面板要讓給它。
    /// </summary>
    public bool IsBrowsing => _mode == PreviewMode.Browse;

    private bool IsShowing => _agent is not null;

    /// <summary>取得這個編輯器的預覽；不是 WPF 編輯器時回傳 null。</summary>
    public static SqlStructurePreview? GetOrCreate(ITextView textView, IServiceProvider serviceProvider)
    {
        if (textView is not IWpfTextView wpfView || wpfView.IsClosed)
        {
            return null;
        }

        return wpfView.Properties.GetOrCreateSingletonProperty(
            typeof(SqlStructurePreview),
            () => new SqlStructurePreview(wpfView, serviceProvider));
    }

    /// <summary>取得已經建立的預覽；沒有就回傳 null，不建立。</summary>
    public static SqlStructurePreview? Peek(ITextView textView)
    {
        return textView is IWpfTextView wpfView &&
               wpfView.Properties.TryGetProperty<SqlStructurePreview>(
                   typeof(SqlStructurePreview),
                   out var preview)
            ? preview
            : null;
    }

    /// <summary>
    /// 畫面上的預覽是不是正錨在這個位置的名稱上；任何執行緒都可以問。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留提示用它讓位：預覽已經攤開這個名稱的完整內容，同一個名稱上再冒出一個
    /// 小提示只會蓋住預覽的一角。只讀一個欄位與不可變的快照，不碰畫面。
    /// </remarks>
    public bool IsShowingAt(SnapshotPoint point)
    {
        if (_shownAnchor is not { } anchor || !ReferenceEquals(anchor.TextBuffer, point.Snapshot.TextBuffer))
        {
            return false;
        }

        var span = anchor.GetSpan(point.Snapshot);
        return PreviewLifecycle.IsOnAnchor(span.Start, span.End, point.Position);
    }

    /// <summary>
    /// 趁閒置時把視窗先建好。
    /// </summary>
    /// <remarks>
    /// 建立整棵 WPF 樹（五個分頁、資料格範本、配色）放在使用者按下向右鍵的那一刻做，
    /// 就等於在他最期待「立刻出現」的時候卡一下。改在建議清單第一次開啟之後、
    /// 以 <see cref="DispatcherPriority.ApplicationIdle"/> 排進佇列——
    /// 那是兩次按鍵之間 UI 執行緒真的沒事做的時候，使用者感覺不到。
    /// </remarks>
    public void Warmup()
    {
        // 清單每開一次就呼叫一次，但要建的東西只有一份；沒有這個旗標就會在佇列裡
        // 疊起一整排最後全部落空的閒置工作。
        if (_closed || _control is not null || _warmupQueued)
        {
            return;
        }

        _warmupQueued = true;
        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => SqlAssistPlatformGuard.Run(
                "預先建立結構預覽",
                () =>
                {
                    _warmupQueued = false;
                    EnsureControl();
                })));
    }

    /// <summary>
    /// 記住 broker 最近觸發的清單，但尚不取得 ownership。
    /// </summary>
    /// <remarks>
    /// CompletionTriggered 也會為其他來源發出；只記候選可讓過期的 SqlAssist
    /// description callback 被拒絕，又不會把原生 session 誤認成自己的生命週期。
    /// </remarks>
    public void ObserveSession(IAsyncCompletionSession session)
    {
        if (_closed || session is null || session.IsDismissed)
        {
            return;
        }

        Invoke(() =>
        {
            if (_session is { } current && !ReferenceEquals(current, session))
            {
                ReleaseSession(PreviewSignal.SessionStarted);
            }

            SetObservedSession(session);
        });
    }

    /// <summary>建議來源參與 session 時就先確認 ownership，不等延後載入的 description。</summary>
    public void OwnSession(IAsyncCompletionSession session, SqlMetadataService metadataService)
    {
        if (_closed ||
            session is null ||
            session.IsDismissed ||
            !ReferenceEquals(session.TextView, _view))
        {
            return;
        }

        Invoke(
            () =>
            {
                if (_closed || session.IsDismissed)
                {
                    return;
                }

                // Context 可能在資料庫查詢後才完成；舊 session 不得覆寫後來已觀察到的清單。
                if ((_observedSession is not null && !ReferenceEquals(_observedSession, session)) ||
                    (_session is not null && !ReferenceEquals(_session, session)))
                {
                    return;
                }

                SetObservedSession(session);
                TrackSession(session);
                if (ReferenceEquals(_session, session))
                {
                    _selectionService = metadataService;
                    // Context 尚在完成中也沒關係：背景 GetComputedItems 會等待 model，UI 不阻塞。
                    BeginReconcile(session, cancelExpandIntent: false);
                }
            });
    }

    /// <summary>
    /// 處理向右鍵的展開意圖；選取仍在背景對帳時先吞鍵，驗證成功後再展開。
    /// </summary>
    public bool RequestExpand(IAsyncCompletionSession? session)
    {
        if (session is not { IsDismissed: false } || !ReferenceEquals(_session, session))
        {
            return false;
        }

        if (_selectedItemHasContent)
        {
            return Expand(PreviewTrigger.CompletionArrow);
        }

        if (!_selectionPending)
        {
            return false;
        }

        _expandWhenSelectionReady = true;
        QueueSelectionRefresh(session);
        return true;
    }

    /// <summary>清單選取即將由鍵盤或滑鼠改變時，先讓舊物件失效，避免右鍵讀到上一項。</summary>
    public void InvalidateSelection(IAsyncCompletionSession? session)
    {
        if (session is null)
        {
            return;
        }

        Invoke(() => BeginReconcile(session, cancelExpandIntent: true));
    }

    /// <summary>只有 SqlAssist item 的 callback 才會走到這裡並正式接管 session。</summary>
    /// <remarks>
    /// 接管的只是選取的記帳。畫面上若是指名打開或釘住的預覽，它照自己的規則留著：
    /// 在那個名稱上按 Ctrl+空白鍵叫出清單，不該把使用者正在看的東西收掉。
    /// </remarks>
    private void TrackSession(IAsyncCompletionSession session)
    {
        if (_closed || session is null || session.IsDismissed || ReferenceEquals(_session, session))
        {
            return;
        }

        ReleaseSession(PreviewSignal.SessionStarted);

        if (_observedSession is { } observed)
        {
            observed.Dismissed -= OnObservedSessionEnded;
        }

        _session = session;
        _observedSession = session;
        ResetSelection();
        session.Dismissed += OnSessionEnded;
        session.ItemCommitted += OnSessionItemCommitted;
        session.ItemsUpdated += OnSessionItemsUpdated;
        AttachInputTracking();
    }

    /// <summary>放下目前跟著的清單；清單上展開的預覽跟著收，指名與釘住的照自己的規則。</summary>
    private void ReleaseSession(PreviewSignal signal)
    {
        if (_session is not { } session)
        {
            return;
        }

        session.Dismissed -= OnSessionEnded;
        session.ItemCommitted -= OnSessionItemCommitted;
        session.ItemsUpdated -= OnSessionItemsUpdated;
        _session = null;
        if (ReferenceEquals(_observedSession, session))
        {
            _observedSession = null;
        }

        ResetSelection();
        DetachInputTracking();
        Apply(signal);
    }

    /// <summary>忘掉清單上的選取與所有跟著它的背景工作。</summary>
    private void ResetSelection()
    {
        _selectionGeneration++;
        _expandTimer.Stop();
        _selectionRefresh?.Cancel();
        _selectionRefresh = null;
        _selection = null;
        _selectionService = null;
        _selectedItemHasContent = false;
        _selectionPending = false;
        _expandWhenSelectionReady = false;
    }

    private void OnSessionItemCommitted(object sender, EventArgs eventArgs) =>
        EndSession(sender as IAsyncCompletionSession);

    private void OnSessionEnded(object sender, EventArgs eventArgs) =>
        EndSession(sender as IAsyncCompletionSession);

    private void OnSessionItemsUpdated(object sender, ComputedCompletionItemsEventArgs eventArgs)
    {
        if (sender is not IAsyncCompletionSession session)
        {
            return;
        }

        // 事件從 ThreadPool 發出，eventArgs 可能已落後於剛發生的方向鍵操作。
        // 不直接套用它攜帶的項目，只把它當成「平台已完成一輪計算」並重新對帳 recent model。
        Invoke(() => BeginReconcile(session, cancelExpandIntent: false));
    }

    private void OnTextBufferChanged(object sender, TextContentChangedEventArgs eventArgs)
    {
        // 涵蓋輸入、Backspace、貼上與復原；等平台更新篩選後再於背景讀最新選取。
        if (_session is { } session)
        {
            Invoke(() => BeginReconcile(session, cancelExpandIntent: true));
        }

        // 游標跟著編輯位移時平台不發游標事件，所以文字變了也要重新看一次錨點。
        QueueAnchorCheck();
    }

    private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs eventArgs) =>
        QueueAnchorCheck();

    /// <summary>
    /// 指名打開的預覽：本輪命令結束後看游標與錨點，離開了就收。
    /// </summary>
    /// <remarks>
    /// 排到命令之後才判斷，同一次按鍵的游標與文字事件併成一次；當下文字與游標都還沒定案。
    /// 其餘狀態不必看：清單上展開的跟著清單走，釘住的只有使用者自己關。
    /// </remarks>
    private void QueueAnchorCheck()
    {
        if (_closed || _anchorCheckQueued || _mode != PreviewMode.Named)
        {
            return;
        }

        _anchorCheckQueued = true;
        TextViewDispatch.AfterCurrentCommand(_view, "檢查結構預覽的錨點", _ =>
        {
            _anchorCheckQueued = false;
            CheckAnchor();
        });
    }

    private void CheckAnchor()
    {
        if (_closed || _mode != PreviewMode.Named || _anchor is not { } anchor)
        {
            return;
        }

        var caret = _view.Caret.Position.BufferPosition;
        if (!ReferenceEquals(anchor.TextBuffer, caret.Snapshot.TextBuffer))
        {
            return;
        }

        var span = anchor.GetSpan(caret.Snapshot);
        if (!string.Equals(span.GetText(), _anchorText, StringComparison.Ordinal))
        {
            Apply(PreviewSignal.AnchorEdited);
            return;
        }

        if (!PreviewLifecycle.IsOnAnchor(span.Start, span.End, caret.Position))
        {
            Apply(PreviewSignal.CaretLeftAnchor);
        }
    }

    /// <summary>把一個訊號交給生命週期規則；它說收就收。</summary>
    private void Apply(PreviewSignal signal)
    {
        if (PreviewLifecycle.Closes(_mode, signal))
        {
            Close(restoreEditorFocus: signal == PreviewSignal.Dismiss);
        }
    }

    private void OnObservedSessionEnded(object sender, EventArgs eventArgs)
    {
        if (sender is not IAsyncCompletionSession expected)
        {
            return;
        }

        Invoke(() =>
        {
            if (ReferenceEquals(_observedSession, expected) && !ReferenceEquals(_session, expected))
            {
                expected.Dismissed -= OnObservedSessionEnded;
                _observedSession = null;
            }
        });
    }

    private void EndSession(IAsyncCompletionSession? expectedSession)
    {
        Invoke(() =>
        {
            if (_session is not { } session ||
                expectedSession is not null && !ReferenceEquals(session, expectedSession))
            {
                return;
            }

            ReleaseSession(PreviewSignal.SessionEnded);
        });
    }

    /// <summary>平台要求某項說明時，重新對帳 completion recent model 的實際選取。</summary>
    /// <remarks>
    /// Description callback 可能延遲或亂序，不能直接相信它帶來的 item；只用它確認
    /// metadata service 與 session，再由背景讀取平台最新模型。
    /// </remarks>
    public void ReconcileSelection(
        IAsyncCompletionSession session,
        SqlMetadataService metadataService)
    {
        if (_closed)
        {
            return;
        }

        Invoke(() =>
        {
            if (session.IsDismissed || !ReferenceEquals(_session, session))
            {
                return;
            }

            // Description callback 可能在非同步等待後才回來；舊 session 不得接管新清單。
            if (_observedSession is not null && !ReferenceEquals(_observedSession, session))
            {
                return;
            }

            _selectionService = metadataService;
            BeginReconcile(session, cancelExpandIntent: false);
        });
    }

    /// <summary>只套用已由 recent model 驗證過的項目；必須在 UI 執行緒。</summary>
    private void ApplyVerifiedSelection(
        IAsyncCompletionSession session,
        SqlPreviewSubject? subject,
        SqlMetadataService metadataService)
    {
        if (_closed || session.IsDismissed || !ReferenceEquals(_session, session))
        {
            return;
        }

        var expandWhenReady = _expandWhenSelectionReady;
        _selectionPending = false;
        _expandWhenSelectionReady = false;
        _selectedItemHasContent = subject is not null;

        // 平台一次換選取會通知好幾輪，多數輪次解析出來的是同一個東西；
        // 同一個就留著原本那一份，畫面才認得出「已經是它了」而不重畫。
        if (!SqlPreviewSubject.IsSame(_selection, subject) ||
            !ReferenceEquals(_selectionService, metadataService))
        {
            _selectionGeneration++;
            _selection = subject;
            _selectionService = metadataService;
        }

        // 向右鍵與「停夠久」只是兩種觸發方式，展開之後做的事完全一樣，所以兩條都只走到
        // Expand()。合成同一個旗標則不行：向右鍵的意圖要跨過「對帳還沒完成」那段空窗
        // （先吞鍵、驗證成功再補展開），而倒數是對帳完成之後才起算的。
        if (expandWhenReady && subject is not null && Expand(PreviewTrigger.CompletionArrow))
        {
            return;
        }

        if (_mode == PreviewMode.Browse)
        {
            ShowSelection();
            return;
        }

        var settings = SqlAssistSettingsStore.Current;
        if (!settings.Enabled ||
            settings.PreviewMode != SqlPreviewMode.Delay ||
            subject is null ||
            !PreviewLifecycle.CanReplace(_mode, PreviewTrigger.CompletionDelay))
        {
            return;
        }

        // 延遲模式：停在同一項夠久才展開。掃過去的那幾項連查詢都不會送出。
        // 同一項的重複通知也走到這裡，倒數因此重新起算——代價是多等一次對帳的幾毫秒，
        // 換到的是「按了方向鍵就一定重新計時」這個使用者真正在感覺的規則。
        _expandGeneration = _selectionGeneration;
        _expandTimer.Stop();
        _expandTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Max(MinimumExpandDelayMilliseconds, settings.PreviewDelayMilliseconds));
        _expandTimer.Start();
    }

    /// <summary>
    /// 在建議清單上展開預覽；已經展開、這一項沒有東西可畫或被擋下時回傳 false，
    /// 讓按鍵照原本的方式往下走。
    /// </summary>
    private bool Expand(PreviewTrigger trigger)
    {
        var settings = SqlAssistSettingsStore.Current;
        if (_closed ||
            _mode == PreviewMode.Browse ||
            _selection is not { } selection ||
            _session is not { IsDismissed: false } session ||
            !settings.Enabled ||
            settings.PreviewMode == SqlPreviewMode.Off ||
            !PreviewLifecycle.CanReplace(_mode, trigger))
        {
            return false;
        }

        Show(trigger, session.ApplicableToSpan, selection, _selectionService);
        return true;
    }

    /// <summary>清單上展開時讓畫面跟上選取；同一個東西不重畫，沒有東西可畫時只收視窗。</summary>
    private void ShowSelection()
    {
        if (_selection is not { } selection)
        {
            if (_subject is not null)
            {
                _generation++;
                StopQueryWork();
                _subject = null;
                _metadataService = null;
            }

            RemoveWindow(restoreEditorFocus: false, animate: false);
            return;
        }

        if (ReferenceEquals(_subject, selection) && ReferenceEquals(_metadataService, _selectionService))
        {
            // 畫面已經是這個東西了。重畫等於使用者眼前閃一下；換代還會取消掉剛送出
            // 的查詢，然後再等一次節流重送。
            return;
        }

        if (_session is { IsDismissed: false } session)
        {
            _anchor = session.ApplicableToSpan;
        }

        Display(selection, _selectionService);
    }

    /// <summary>收合清單上展開的預覽；畫面上沒有它時回傳 false，讓向左鍵照常移動游標。</summary>
    /// <remarks>
    /// 展開中但視窗因為這一項沒有結構而收著時，也照常移動游標：使用者眼前沒有東西可以收，
    /// 吞掉這一鍵看起來就是游標卡住了。指名與釘住的預覽不歸向左鍵管。
    /// </remarks>
    public bool Collapse()
    {
        if (_mode != PreviewMode.Browse)
        {
            if (_expandWhenSelectionReady)
            {
                // 向右鍵尚在等背景對帳時，向左鍵代表取消這次展開意圖。
                _expandWhenSelectionReady = false;
                return true;
            }

            return false;
        }

        var wasShowing = IsShowing;
        Close(restoreEditorFocus: false);
        return wasShowing;
    }

    /// <summary>編輯器裡按 Esc 而沒有清單時：有預覽就收掉並吃掉這一鍵。</summary>
    public bool Dismiss()
    {
        if (_mode == PreviewMode.Hidden)
        {
            return false;
        }

        var wasShowing = IsShowing;
        Apply(PreviewSignal.Dismiss);
        return wasShowing;
    }

    /// <summary>
    /// 使用者指名要看的內容：Ctrl+F12、Ctrl＋點擊、滑鼠停留提示的連結與工具選單。
    /// </summary>
    /// <remarks>
    /// 與建議清單共用同一個視窗、同一份資料路徑，差別只在錨點與活多久。內建說明沒有中繼
    /// 資料，<paramref name="metadataService"/> 傳 null；留著上一個的話，後續的查詢節流會把
    /// 畫面換回上一個資料表。
    /// </remarks>
    public void Open(
        PreviewTrigger trigger,
        ITrackingSpan anchor,
        SqlPreviewSubject subject,
        SqlMetadataService? metadataService)
    {
        if (_closed || anchor is null || subject is null)
        {
            return;
        }

        Invoke(() =>
        {
            if (!_closed && PreviewLifecycle.CanReplace(_mode, trigger))
            {
                Show(trigger, anchor, subject, metadataService);
            }
        });
    }

    /// <summary>所有入口的終點：決定這一次活多久、錨在哪，再畫出來。</summary>
    private void Show(
        PreviewTrigger trigger,
        ITrackingSpan anchor,
        SqlPreviewSubject subject,
        SqlMetadataService? metadataService)
    {
        _mode = PreviewLifecycle.ModeFor(trigger);
        _anchor = anchor;
        _anchorText = anchor.GetSpan(anchor.TextBuffer.CurrentSnapshot).GetText();
        _control?.SetPinned(false);

        if (ReferenceEquals(_subject, subject) && ReferenceEquals(_metadataService, metadataService) && IsShowing)
        {
            // 清單上展開的正是畫面上那一份：只換錨點與規則，不重畫。
            ShowAgent();
            return;
        }

        Display(subject, metadataService);
    }

    /// <summary>換畫面上的內容；過期的查詢與節流一律作廢。</summary>
    private void Display(SqlPreviewSubject subject, SqlMetadataService? metadataService)
    {
        _generation++;
        StopQueryWork();
        _subject = subject;
        _metadataService = metadataService;
        ShowSubject(subject, metadataService);
    }

    /// <summary>
    /// 圖釘：釘住之後只有使用者自己關；放開回到指名，照游標規則收。
    /// </summary>
    private void TogglePin()
    {
        if (_mode == PreviewMode.Hidden)
        {
            return;
        }

        _mode = PreviewLifecycle.TogglePin(_mode);
        if (_mode == PreviewMode.Named && _anchor is { } anchor)
        {
            // 釘住期間名稱可能被改過；放開時以眼前的文字為準，不因為舊的差異立刻收掉。
            _anchorText = anchor.GetSpan(anchor.TextBuffer.CurrentSnapshot).GetText();
        }

        _control?.SetPinned(_mode == PreviewMode.Pinned);
    }

    /// <summary>收掉預覽並放下展開意圖；本來就沒顯示時回傳 false。</summary>
    private bool Close(bool restoreEditorFocus)
    {
        _generation++;
        StopQueryWork();
        _expandTimer.Stop();
        _expandWhenSelectionReady = false;
        _mode = PreviewMode.Hidden;
        _subject = null;
        _metadataService = null;
        _anchorText = null;
        return RemoveWindow(restoreEditorFocus, animate: true);
    }

    /// <summary>只收掉視窗，狀態不動；本來就沒顯示時回傳 false。</summary>
    /// <param name="animate">使用者看得出「關了」的時候才縮回錨點；清單上路過關鍵字時直接收。</param>
    private bool RemoveWindow(bool restoreEditorFocus, bool animate)
    {
        _shownAnchor = null;
        if (_agent is not { } agent || _manager is not { } manager)
        {
            return false;
        }

        // 先放開再移除：移除會發 AgentChanged，而那個處理常式把「agent 不見了」當成外力
        // 收掉並清空狀態。自己收的不能走那條，否則選到關鍵字時暫時收起視窗，會連帶讓
        // 移回資料表時不再出現。
        _agent = null;

        SqlAssistPlatformGuard.Run("收起結構預覽", () =>
        {
            var hadFocus = agent.HasFocus;
            agent.AnimateNextHide = animate;
            manager.RemoveAgent(agent);

            // 不論 manager 是否已先移除，都要確定關掉 HWND，不留下孤兒 Popup。
            agent.Dispose();

            // 焦點在預覽裡時直接移除，鍵盤會落到不明的地方；還給編輯器。
            // 只有使用者從預覽主動關閉才還焦點；session 結束不能搶回 SSMS。
            if (restoreEditorFocus && hadFocus && !_view.IsClosed && _view.VisualElement.IsVisible)
            {
                _view.VisualElement.Focus();
            }
        });

        return true;
    }

    private void StopQueryWork()
    {
        _queryTimer.Stop();
        _loading?.Cancel();
    }

    private void SetObservedSession(IAsyncCompletionSession? session)
    {
        if (ReferenceEquals(_observedSession, session))
        {
            return;
        }

        if (_observedSession is { } previous && !ReferenceEquals(previous, _session))
        {
            previous.Dismissed -= OnObservedSessionEnded;
        }

        _observedSession = session;
        if (session is not null && !ReferenceEquals(session, _session))
        {
            session.Dismissed += OnObservedSessionEnded;
        }
    }

    /// <summary>
    /// 選取可能換人了：讓「右鍵立刻展開」失效，並到背景去問平台真正選到誰。
    /// </summary>
    /// <remarks>
    /// 刻意不動 <see cref="_selection"/>、查詢節流與載入工作，也不碰畫面。平台換一次選取
    /// 會從方向鍵命令、說明 callback 與 <c>ItemsUpdated</c> 分別通知一次；只要其中一條
    /// 先把畫面清成「正在取得目前建議項目…」，另外兩條就會讓同一個物件再重畫一次——
    /// 使用者看到的是每按一次方向鍵閃一下，而且剛送出的查詢會被取消再送一次。
    /// 該不該換內容留給 <see cref="ApplyVerifiedSelection"/>，只有它知道新舊是不是同一個。
    ///
    /// 舊物件留在畫面上不會被誤用：這裡把 <see cref="_selectedItemHasContent"/> 壓成
    /// false，向右鍵因此走「等對帳完成再展開」那條路，不會拿上一項展開。
    /// </remarks>
    private void BeginReconcile(IAsyncCompletionSession session, bool cancelExpandIntent)
    {
        if (!ReferenceEquals(_session, session) || session.IsDismissed)
        {
            return;
        }

        if (cancelExpandIntent)
        {
            _expandWhenSelectionReady = false;
        }

        _selectedItemHasContent = false;
        _selectionPending = true;

        // 「停夠久才自動展開」的倒數前提是使用者停在同一項上，換了就重新起算——
        // 不取消的話，倒數會在對帳完成前到期，於是展開的是上一項。
        _expandTimer.Stop();

        QueueSelectionRefresh(session);
    }

    /// <summary>對帳結果確定沒有東西可畫；清單上展開時連畫面一起收。</summary>
    private void ClearSelection(IAsyncCompletionSession session)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        _selectionGeneration++;
        _expandTimer.Stop();
        _selection = null;
        _selectedItemHasContent = false;
        _selectionPending = false;
        _expandWhenSelectionReady = false;

        if (_mode == PreviewMode.Browse)
        {
            ShowSelection();
        }
    }

    /// <summary>
    /// 等平台先處理完這次鍵盤／滑鼠輸入，再從背景取得最新選取。
    /// </summary>
    /// <remarks>
    /// <see cref="IAsyncCompletionSession.GetComputedItems"/> 可能等待正在執行的篩選，
    /// 絕不能放在按鍵的 UI 執行緒。背景等待同時補足 ItemsUpdated 不會為單純上下移動
    /// 觸發的缺口，也讓「點回同一項」不會永遠停在失效狀態。
    /// 新的一輪會取消舊的一輪，所以只認最新那一份 <see cref="_selectionRefresh"/>。
    /// </remarks>
    private void QueueSelectionRefresh(IAsyncCompletionSession session)
    {
        _selectionRefresh?.Cancel();
        var source = new CancellationTokenSource();
        _selectionRefresh = source;

        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!IsCurrentRefresh(session, source))
                {
                    if (ReferenceEquals(_selectionRefresh, source))
                    {
                        _selectionRefresh = null;
                    }

                    source.Dispose();
                    return;
                }

                SqlAssistPlatformGuard.Begin(
                    NotificationCatalog.RefreshingPreviewSelection,
                    () => RefreshSelectionAsync(session, source),
                    NotificationKind.Preview, NotificationOrigin.Typing, NotificationLevel.Debug,
                    ActiveSqlEditor.GetDocumentName(_view));
            }));
    }

    private bool IsCurrentRefresh(IAsyncCompletionSession session, CancellationTokenSource source) =>
        !source.IsCancellationRequested &&
        ReferenceEquals(_selectionRefresh, source) &&
        ReferenceEquals(_session, session) &&
        !session.IsDismissed;

    private async Task RefreshSelectionAsync(
        IAsyncCompletionSession session,
        CancellationTokenSource source)
    {
        try
        {
            var computed = await Task.Run(
                    () => session.GetComputedItems(source.Token),
                    source.Token)
                .ConfigureAwait(false);

            await _view.VisualElement.Dispatcher.InvokeAsync(
                () =>
                {
                    if (!IsCurrentRefresh(session, source))
                    {
                        return;
                    }

                    // 先解除目前工作，再套用結果；Apply/Clear 取消 pending work 時不會反向取消自己。
                    _selectionRefresh = null;

                    var selected = computed.SelectedItem;
                    if (selected is not null &&
                        selected.Properties.TryGetProperty<SqlSuggestion>(
                            SqlAsyncCompletionSource.SuggestionKey,
                            out var suggestion) &&
                        _selectionService is { } metadataService)
                    {
                        // 只有此處同時驗證過 source 與 recent model，才可更新選取。
                        ApplyVerifiedSelection(
                            session,
                            SqlSuggestionTarget.Describe(suggestion),
                            metadataService);
                    }
                    else
                    {
                        ClearSelection(session);
                    }
                },
                DispatcherPriority.Normal);
        }
        finally
        {
            var dispatcher = _view.VisualElement.Dispatcher;
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                await dispatcher.InvokeAsync(
                    () =>
                    {
                        if (ReferenceEquals(_selectionRefresh, source))
                        {
                            _selectionRefresh = null;
                        }
                    },
                    DispatcherPriority.Normal);
            }

            source.Dispose();
        }
    }

    private void AttachInputTracking()
    {
        if (_inputTrackingAttached)
        {
            return;
        }

        _inputTrackingAttached = true;
        InputManager.Current.PreProcessInput += OnPreProcessInput;
    }

    private void DetachInputTracking()
    {
        if (!_inputTrackingAttached)
        {
            return;
        }

        _inputTrackingAttached = false;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs eventArgs)
    {
        if (eventArgs.StagingItem.Input is not MouseButtonEventArgs mouse ||
            mouse.ButtonState != MouseButtonState.Pressed ||
            !_view.IsMouseOverViewOrAdornments ||
            _agent is { IsMouseOver: true } ||
            _session is not { } session)
        {
            return;
        }

        SqlAssistPlatformGuard.Run(
            "滑鼠切換建議項目",
            () => BeginReconcile(session, cancelExpandIntent: true));
    }

    /// <summary>
    /// 把目前的主體畫出來。
    /// </summary>
    /// <remarks>
    /// 所有入口（向右鍵、停夠久、停留提示的連結、Ctrl+F12、Ctrl＋點擊）的終點都是這裡，所以
    /// 「換內容之前要先停掉什麼」與「畫什麼」都只有這一份。
    ///
    /// 物件由便宜到昂貴依序嘗試：第四層快取命中就直接畫完；只有第二層命中就先畫欄位，
    /// 索引與外來鍵稍後補上；兩層都沒有就先畫標題，等節流計時器到期才查資料庫。
    /// 使用者按著方向鍵一路往下時，中途的每一項都不會送出查詢。
    /// </remarks>
    /// <param name="metadataService">
    /// 內建說明不需要，傳 null；物件在對帳完成前也可能還沒有，那時只畫得出標題。
    /// </param>
    private void ShowSubject(SqlPreviewSubject subject, SqlMetadataService? metadataService)
    {
        var control = EnsureControl();

        if (control is null)
        {
            return;
        }

        // 內建說明是隨組件發布的一份資料：查表就有，畫完就結束，不起節流計時器。
        if (subject.BuiltIn is { } doc)
        {
            control.ShowBuiltIn(doc);
            ShowAgent();
            return;
        }

        if (subject.Object is not { } objectInfo)
        {
            return;
        }

        // 指令碼自己宣告的物件不必經過任何一層快取或查詢：答案就在使用者眼前的文字裡。
        if (objectInfo.Kind.IsScriptDeclared())
        {
            ShowDeclared(control, subject, objectInfo);
            return;
        }

        // 對帳還沒把中繼資料服務交過來就先展開了：先把標題畫出來，等它補上。
        if (metadataService is null)
        {
            control.SetTarget(objectInfo);
            ShowAgent();
            return;
        }

        if (metadataService.PeekStructure(objectInfo) is { } structure)
        {
            control.Populate(structure);
            ShowAgent();
            return;
        }

        control.SetTarget(objectInfo);

        if (metadataService.PeekDetail(objectInfo) is { } detail)
        {
            control.PopulatePartial(detail);
        }

        ShowAgent();

        _queryGeneration = _generation;
        _queryTimer.Start();
    }

    /// <summary>
    /// 畫一個這份指令碼自己宣告的物件。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留與 Ctrl+F12 在定位那一步就把明細讀好了，直接畫；建議清單那條入口
    /// 只知道名稱，這裡才去問名冊。兩條路徑最後畫的是同一份東西。
    /// </remarks>
    private void ShowDeclared(
        SqlStructurePreviewControl control,
        SqlPreviewSubject subject,
        SqlObjectInfo objectInfo)
    {
        if (subject.Script is null ||
            !SqlPreviewSubject.IsSameObject(subject.Script.Object, objectInfo))
        {
            subject.Script = FindDeclared(objectInfo.Name) is { } detail
                ? new SqlObjectStructure(detail)
                : null;
        }

        if (subject.Script is { } declared)
        {
            control.Populate(declared);
        }
        else if (_mode == PreviewMode.Browse)
        {
            // 從建議清單路過的：沒有結構就不佔位置，與關鍵字同一條規則。
            RemoveWindow(restoreEditorFocus: false, animate: false);
            return;
        }
        else
        {
            // 使用者指名要看的（停留提示、Ctrl+F12）：名稱認得出來、資料行讀不出來——
            // SELECT * INTO #Loan FROM dbo.Loan 的欄位只有中繼資料知道，而這條路徑不等查詢。
            // 說出實情，不要畫一個空的結構讓人以為它真的沒有欄位。
            control.ShowMessage(
                objectInfo.QualifiedName,
                PreviewText.ScriptDeclaredNoColumns);
        }

        ShowAgent();
    }

    /// <summary>問這份文字宣告了什麼；名冊照文字版本留著，同一個版本只掃一次。</summary>
    private SqlObjectDetail? FindDeclared(string name)
    {
        var snapshot = _view.TextBuffer.CurrentSnapshot;

        if (!ReferenceEquals(_declarationsSnapshot, snapshot))
        {
            _declarationsSnapshot = snapshot;
            _declarations = SqlScriptDeclarations.Create(snapshot.GetText());
        }

        return _declarations?.Find(name);
    }

    private void OnExpandTimerTick(object sender, EventArgs eventArgs)
    {
        _expandTimer.Stop();

        var settings = SqlAssistSettingsStore.Current;
        if (_expandGeneration == _selectionGeneration &&
            settings.Enabled &&
            settings.PreviewMode == SqlPreviewMode.Delay)
        {
            SqlAssistPlatformGuard.Run("結構預覽操作", () => Expand(PreviewTrigger.CompletionDelay));
        }
    }

    private void OnQueryTimerTick(object sender, EventArgs eventArgs)
    {
        _queryTimer.Stop();

        if (_queryGeneration == _generation &&
            _subject is { Object: { } target } &&
            _metadataService is { } metadataService &&
            _mode != PreviewMode.Hidden)
        {
            BeginLoad(target, metadataService);
        }
    }

    private void BeginLoad(SqlObjectInfo objectInfo, SqlMetadataService metadataService)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var source = new CancellationTokenSource();
        _loading = source;
        var generation = _generation;

        // 取消一律當成正常結束：換了物件或收起了視窗，什麼都不用做。
        SqlAssistPlatformGuard.Begin(
            NotificationCatalog.LoadingStructurePreview,
            () => LoadAsync(objectInfo, metadataService, source, generation),
            NotificationKind.Preview, NotificationOrigin.Typing, NotificationLevel.Info,
            ActiveSqlEditor.GetDocumentName(_view), objectInfo.QualifiedName);
    }

    private async Task LoadAsync(
        SqlObjectInfo objectInfo,
        SqlMetadataService metadataService,
        CancellationTokenSource source,
        long generation)
    {
        var cancellationToken = source.Token;
        var structure = await metadataService
            .GetStructureAsync(objectInfo, cancellationToken, NotificationOrigin.Typing)
            .ConfigureAwait(false);

        await _view.VisualElement.Dispatcher.InvokeAsync(
            () =>
            {
                // 等待期間使用者可能已經移到別的項目，那就不要蓋掉他正在看的東西。
                if (cancellationToken.IsCancellationRequested ||
                    generation != _generation ||
                    !ReferenceEquals(_loading, source) ||
                    !SqlPreviewSubject.IsSameObject(_subject?.Object, objectInfo) ||
                    !ReferenceEquals(_metadataService, metadataService) ||
                    _control is not { } control)
                {
                    return;
                }

                if (structure is null)
                {
                    control.ShowMessage(
                        objectInfo.QualifiedName,
                        PreviewText.NoConnection);
                    return;
                }

                control.Populate(structure);
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private SqlStructurePreviewControl? EnsureControl()
    {
        if (_closed)
        {
            return null;
        }

        if (_control is not null)
        {
            return _control;
        }

        return SqlAssistPlatformGuard.Create("建立結構預覽", () =>
        {
            var control = new SqlStructurePreviewControl(_view);
            control.ResizeStarted += OnResizeStarted;
            control.ResizeDelta += OnResizeDelta;
            control.ResizeCompleted += OnResizeCompleted;
            control.SizeResetRequested += OnSizeResetRequested;
            control.CloseRequested += OnCloseRequested;
            control.PinToggled += OnPinToggled;
            _control = control;
            return control;
        });
    }

    private void OnCloseRequested(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("關閉結構預覽", () => Apply(PreviewSignal.Dismiss));
    }

    private void OnPinToggled(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("釘住結構預覽", TogglePin);
    }

    private void OnResizeStarted(object sender, PreviewResizeDragEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("開始調整結構預覽", () =>
        {
            if (_agent is { } agent)
            {
                _resizeStartWidth = agent.CurrentWidth;
                _resizeStartHeight = agent.CurrentHeight;

                // 要的是「版面計算的結果」，所以在 BeginResize 之前取；拖曳一開始
                // 這兩個旗標就會被使用者的意圖蓋掉。
                _resizeStartWidthConstrained = agent.WidthConstrained;
                _resizeStartHeightConstrained = agent.HeightConstrained;
                agent.BeginResize(eventArgs.Corner);
            }
        });
    }

    private void OnResizeDelta(object sender, PreviewResizeDragEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run(
            "調整結構預覽",
            () => _agent?.Resize(eventArgs.HorizontalChange, eventArgs.VerticalChange));
    }

    private void OnResizeCompleted(object sender, PreviewResizeDragEventArgs eventArgs)
    {
        if (_agent is not { } agent)
        {
            return;
        }

        SqlAssistPlatformGuard.Run("儲存結構預覽尺寸", () =>
        {
            agent.CompleteResize(eventArgs.Canceled);
            if (eventArgs.Canceled)
            {
                return;
            }

            var widthDelta = Math.Abs(agent.CurrentWidth - _resizeStartWidth);
            var heightDelta = Math.Abs(agent.CurrentHeight - _resizeStartHeight);

            // 版面壓縮出來的尺寸不是偏好。使用者拖不出比限制更大的值，所以在被壓縮的
            // 軸上拖出來的任何數字都摻了「這裡只放得下這麼多」——寫回去等於每遇到一次
            // 空間不足，記住的尺寸就被永久縮小一次。代價是在被壓縮的軸上刻意縮小不會
            // 被記住，因為分不出「他想要 350」與「這裡只放得下 400」。
            var widthChanged = widthDelta >= 0.5 && !_resizeStartWidthConstrained;
            var heightChanged = heightDelta >= 0.5 && !_resizeStartHeightConstrained;

            // 存到哪一組看實際落點，不看設定值：側邊放不下而退回上下時，
            // 使用者拖出來的是上下擺放的尺寸。
            var effectivePlacement = agent.EffectivePlacement;

            // 上下擺放尚未手動調寬時，寬度是「自動延伸到編輯器右側」這個狀態，不是一個
            // 數值。角落握把一定同時動到兩軸，只想拉高的人也會順手帶進幾個像素的水平
            // 位移，於是自動寬度被換成一個固定值，而且再也回不去。門檻用握把自己的邊長：
            // 位移不到一個握把就當作沒有要拖那一軸。其餘情況維持 0.5，避免拖完之後
            // 尺寸又跳回上一個偏好值。
            if (widthChanged &&
                effectivePlacement == SqlPreviewPlacement.Stacked &&
                PreviewWindowState.StackedWidth is null &&
                widthDelta < SqlStructurePreviewControl.GripSize)
            {
                widthChanged = false;
            }

            if (!widthChanged && !heightChanged)
            {
                return;
            }

            PreviewWindowState.Save(
                effectivePlacement,
                widthChanged ? agent.CurrentWidth : (double?)null,
                heightChanged ? agent.CurrentHeight : (double?)null);
            UpdateAgentPreferences(agent);
        });
    }

    private void OnSizeResetRequested(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("重設結構預覽尺寸", () =>
        {
            // 重設的也是眼前這個視窗那一組；退回上下時雙擊握把，要回到的是上下的預設值。
            PreviewWindowState.Reset(_agent?.EffectivePlacement ?? Placement);
            if (_agent is { } agent)
            {
                UpdateAgentPreferences(agent);
                agent.RequestReposition();
            }
        });
    }

    /// <summary>把自訂 Agent 掛上 reservation stack；已掛著時只更新狀態並重排。</summary>
    private void ShowAgent()
    {
        // 清單上展開的錨點跟著清單走：使用者繼續打字時 ApplicableToSpan 會跟著長。
        if (_mode == PreviewMode.Browse && _session is { IsDismissed: false } session)
        {
            _anchor = session.ApplicableToSpan;
        }

        if (_control is not { } control || _anchor is not { } anchor || _view.IsClosed)
        {
            return;
        }

        SqlAssistPlatformGuard.Run("顯示結構預覽", () =>
        {
            control.ApplyFontSize(SqlAssistSettingsStore.Current.PreviewFontSize);

            if (_manager is null)
            {
                _manager = _view.GetSpaceReservationManager(
                    SqlPreviewDefinitions.SpaceReservationManagerName);
                if (_manager is null)
                {
                    return;
                }

                _manager.AgentChanged += OnAgentChanged;
            }

            if (_agent is { } existing)
            {
                UpdateAgentPreferences(existing);
                existing.RequestReposition();
                _shownAnchor = anchor;
                return;
            }

            // 上一個視窗可能還在縮回錨點；內容只有一份，先讓它立刻收完才掛得上新的承載視窗。
            control.CompleteExit();

            var created = new SqlPreviewPopupAgent(_view, _manager, anchor, control);
            UpdateAgentPreferences(created);
            _agent = created;
            var added = SqlAssistPlatformGuard.Run(
                "掛上結構預覽",
                () =>
                {
                    _manager.AddAgent(created);
                    return true;
                },
                fallback: false);
            if (!added)
            {
                if (ReferenceEquals(_agent, created))
                {
                    _agent = null;
                }

                if (_manager.Agents.Contains(created))
                {
                    _manager.RemoveAgent(created);
                }

                created.Dispose();
                return;
            }

            _shownAnchor = anchor;
        });
    }

    private void UpdateAgentPreferences(SqlPreviewPopupAgent agent)
    {
        if (_anchor is not { } anchor)
        {
            return;
        }

        // 兩組都給。側邊放不下而退回上下時，尺寸要跟著換成上下那一組，
        // 而那件事要等定位算完才知道，所以決定權在 Agent 那一端。
        agent.Update(
            anchor,
            Placement,
            PreviewWindowState.Preferred(SqlPreviewPlacement.Beside),
            PreviewWindowState.Preferred(SqlPreviewPlacement.Stacked));
    }

    private void OnViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs eventArgs) =>
        QueueLayoutUpdate();

    private void OnViewportGeometryChanged(object sender, EventArgs eventArgs) =>
        QueueLayoutUpdate();

    private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs eventArgs) =>
        QueueLayoutUpdate();

    /// <summary>合併同一輪的 Layout／Viewport／Zoom 通知，兩種擺放都重算完整快照。</summary>
    private void QueueLayoutUpdate()
    {
        if (_closed || _layoutUpdateQueued || _agent is null)
        {
            return;
        }

        _layoutUpdateQueued = true;
        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => SqlAssistPlatformGuard.Run(
                "更新結構預覽版面",
                () =>
                {
                    _layoutUpdateQueued = false;
                    if (_closed || _agent is not { } agent)
                    {
                        return;
                    }

                    if (_mode == PreviewMode.Browse && _session is { IsDismissed: false } session)
                    {
                        _anchor = session.ApplicableToSpan;
                    }

                    UpdateAgentPreferences(agent);
                    agent.RequestReposition();
                })));
    }

    /// <summary>
    /// 預覽裡有選取時，由它接手複製。
    /// </summary>
    /// <remarks>
    /// 浮動視窗拿不到鍵盤焦點，Ctrl+C 會落在查詢視窗的命令鏈上而不是預覽裡，
    /// 所以由編輯器那一端把這個命令轉過來。編輯器自己有選取時不搶。
    /// </remarks>
    public bool CopySelectionIfAny()
    {
        if (_agent is not { } agent ||
            (!agent.HasFocus && !agent.IsMouseOver) ||
            _control is not { } control ||
            !control.HasSelection())
        {
            return false;
        }

        control.CopySelection();
        return true;
    }

    /// <summary>
    /// 平台換掉或移除了代理人。
    /// </summary>
    /// <remarks>
    /// 只更新自己的狀態，不試著重新顯示：平台會移除通常代表它判斷此時不該顯示，
    /// 立刻掛回去只會變成一場拉鋸。把狀態清乾淨，下一次使用者主動要求就會是全新的一輪。
    /// </remarks>
    private void OnAgentChanged(object sender, SpaceReservationAgentChangedEventArgs eventArgs)
    {
        if (_agent is not { } current || !ReferenceEquals(eventArgs.OldAgent, current))
        {
            return;
        }

        _agent = eventArgs.NewAgent as SqlPreviewPopupAgent;
        current.Dispose();

        if (_agent is null)
        {
            // 自己收的都先放開了 _agent，走到這裡的只有定位失敗或編輯器拆掉。
            Close(restoreEditorFocus: false);
        }
    }

    /// <summary>確保工作落在 UI 執行緒上；已經在上面就直接執行，不多繞一圈。</summary>
    /// <remarks>
    /// 這些工作都掛在按鍵與滑鼠路徑上，例外冒出去就是一個錯誤對話框。
    /// </remarks>
    private void Invoke(Action action)
    {
        var dispatcher = _view.VisualElement.Dispatcher;

        if (dispatcher.CheckAccess())
        {
            SqlAssistPlatformGuard.Run("結構預覽操作", action);
            return;
        }

        dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() => SqlAssistPlatformGuard.Run("結構預覽操作", action)));
    }

    private void OnViewClosed(object sender, EventArgs eventArgs)
    {
        _closed = true;
        _view.Closed -= OnViewClosed;
        _view.LayoutChanged -= OnViewLayoutChanged;
        _view.ViewportLeftChanged -= OnViewportGeometryChanged;
        _view.ViewportWidthChanged -= OnViewportGeometryChanged;
        _view.ViewportHeightChanged -= OnViewportGeometryChanged;
        _view.ZoomLevelChanged -= OnZoomLevelChanged;
        _view.Caret.PositionChanged -= OnCaretPositionChanged;
        _view.TextBuffer.Changed -= OnTextBufferChanged;
        SqlLanguageSwitch.Changed -= OnLanguageChanged;
        _expandTimer.Stop();
        _expandTimer.Tick -= OnExpandTimerTick;
        _queryTimer.Stop();
        _queryTimer.Tick -= OnQueryTimerTick;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;

        // 名冊抓著那個版本的整份文字，視窗都關了不必再留著。
        _declarationsSnapshot = null;
        _declarations = null;
        // 先放下狀態再放開清單：編輯器已經關了，沒有東西要縮回錨點。
        _mode = PreviewMode.Hidden;
        _shownAnchor = null;
        ReleaseSession(PreviewSignal.SessionEnded);
        SetObservedSession(null);
        ReleaseControl();

        if (_manager is { } manager)
        {
            manager.AgentChanged -= OnAgentChanged;
            _manager = null;
        }
    }

    /// <summary>
    /// 換語言時關掉預覽並丟掉建好的視窗，下一次展開用新語言重建。
    /// </summary>
    /// <remarks>
    /// 分頁標題、欄名與按鈕在建立時取字；開著的那一份就地改字要每個分頁各自重畫，
    /// 而切語言時使用者在設定頁上，預覽本來就不在眼前。
    /// </remarks>
    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        if (_closed) return;
        Close(restoreEditorFocus: false);
        ReleaseControl();
    }

    private void ReleaseControl()
    {
        if (_agent is { } agent)
        {
            if (_manager is { } agentManager)
            {
                agentManager.RemoveAgent(agent);
            }

            agent.Dispose();
            _agent = null;
        }

        if (_control is { } control)
        {
            control.ResizeStarted -= OnResizeStarted;
            control.ResizeDelta -= OnResizeDelta;
            control.ResizeCompleted -= OnResizeCompleted;
            control.SizeResetRequested -= OnSizeResetRequested;
            control.CloseRequested -= OnCloseRequested;
            control.PinToggled -= OnPinToggled;
            control.Dispose();
            _control = null;
        }
    }
}
