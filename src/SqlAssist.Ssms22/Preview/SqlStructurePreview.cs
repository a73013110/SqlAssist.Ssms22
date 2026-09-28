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
/// 顯示誰、它活多久（<see cref="SqlStructurePresenter.Subject"/>、<see cref="_mode"/> 與
/// <see cref="_pinned"/>）。什麼時候收起來只問 <see cref="PreviewLifecycle"/>，這裡只負責把
/// 平台事件翻成它的訊號；畫什麼、怎麼分層載入交給 <see cref="SqlStructurePresenter"/>，
/// 與停靠的工具視窗共用。
/// </remarks>
internal sealed class SqlStructurePreview
{
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
    /// 與查詢節流（在 <see cref="SqlStructurePresenter"/>）分開：清單開著時指名打開的預覽可能還在
    /// 等查詢，共用一個的話在清單上換一次選取就會把那個查詢停掉。
    /// </remarks>
    private readonly DispatcherTimer _expandTimer;

    private PreviewSurface? _surface;
    private SqlStructurePresenter? _presenter;
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

    /// <summary>
    /// 視窗釘在某處：之後的預覽都在這扇窗裡換內容，只有使用者自己關。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="_mode"/> 分開記：清單借用釘住的窗時，內容跟著清單走（<see cref="PreviewMode.Browse"/>），
    /// 窗卻還釘著，清單結束要把 <see cref="_held"/> 還回去。
    /// </remarks>
    private bool _pinned;

    /// <summary>清單借用釘住的窗時，被借走的那一份；只在借用期間不是 null。</summary>
    private HeldPreview? _held;

    /// <summary>正在換位置時要換上的內容；舊位置縮回膠囊之後才畫上去，縮的途中看到的還是舊的。</summary>
    private (SqlPreviewSubject Subject, SqlMetadataService? Service)? _pending;

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

    /// <summary>目前這份文字的指令碼宣告名冊，與它所屬的版本。</summary>
    /// <remarks>
    /// 名冊要掃過整份文字，所以只在真的要畫的時候才建，而且照版本留著：使用者
    /// 按著方向鍵在清單裡上下走時文字一個字都沒動，那一段路上一次都不必重掃。
    /// 反過來，文字一改就得換一份——舊的那一份會交出使用者已經刪掉的宣告。
    /// </remarks>
    private ITextSnapshot? _declarationsSnapshot;

    private SqlScriptDeclarations? _declarations;

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

    /// <summary>清單換 session 或選取時遞增；過期的對帳與自動展開倒數不得越代套用。</summary>
    private long _selectionGeneration;

    private long _expandGeneration;

    /// <summary>拖曳開始時的寬高（DIP）；放開時比對哪一軸真的被拖過。</summary>
    private double _dragStartWidth;

    private double _dragStartHeight;

    private SqlStructurePreview(IWpfTextView view, IServiceProvider serviceProvider)
    {
        _view = view;
        _serviceProvider = serviceProvider;

        _expandTimer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher);
        _expandTimer.Tick += OnExpandTimerTick;

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
        if (_closed || _surface is not null || _warmupQueued)
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
                    EnsureSurface();
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

    /// <summary>把一個訊號交給生命週期規則；它說收就收，說還就把釘住的那一份換回來。</summary>
    private void Apply(PreviewSignal signal)
    {
        switch (PreviewLifecycle.Resolve(_mode, _pinned, signal))
        {
            case PreviewOutcome.Close:
                Close(restoreEditorFocus: signal == PreviewSignal.Dismiss);
                break;
            case PreviewOutcome.ReturnToPin:
                ReturnToPin();
                break;
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

        // 釘住的窗也照樣倒數：停夠久展開的內容借用那扇窗，清單結束再還回去。
        var settings = SqlAssistSettingsStore.Current;
        if (!settings.Enabled ||
            settings.PreviewMode != SqlPreviewMode.Delay ||
            subject is null)
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
            settings.PreviewMode == SqlPreviewMode.Off)
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
            ShowNothingForSelection();
            return;
        }

        if (ReferenceEquals(_presenter?.Subject, selection) && ReferenceEquals(_presenter?.Service, _selectionService))
        {
            // 畫面已經是這個東西了。重畫等於使用者眼前閃一下；換代還會取消掉剛送出
            // 的查詢，然後再等一次節流重送。
            return;
        }

        if (_session is { IsDismissed: false } session)
        {
            _anchor = session.ApplicableToSpan;
        }

        Present(selection, _selectionService, PreviewTransition.None);
    }

    /// <summary>
    /// 清單上選到沒有東西可畫的項目（關鍵字、片段、讀不出資料行的宣告）。
    /// </summary>
    /// <remarks>
    /// 只收視窗、不動 <see cref="PreviewMode.Browse"/>，移回有結構的項目就自己出現。借用釘住的窗時
    /// 換回釘住的那一份，窗不收：那是使用者擺在那裡的，路過一個關鍵字就消失再出現太吵。
    /// </remarks>
    private void ShowNothingForSelection()
    {
        if (_held is { } held)
        {
            Present(held.Subject, held.Service, PreviewTransition.None);
            return;
        }

        _presenter?.Clear();
        RemoveWindow(restoreEditorFocus: false, animate: false);
    }

    /// <summary>收合清單上展開的預覽；畫面上沒有它時回傳 false，讓向左鍵照常移動游標。</summary>
    /// <remarks>
    /// 展開中但視窗因為這一項沒有結構而收著時，也照常移動游標：使用者眼前沒有東西可以收，
    /// 吞掉這一鍵看起來就是游標卡住了。指名與釘住的預覽不歸向左鍵管；借用釘住的窗時收合是
    /// 把釘住的那一份換回來。
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
        Apply(PreviewSignal.Collapse);
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
            if (!_closed)
            {
                Show(trigger, anchor, subject, metadataService);
            }
        });
    }

    /// <summary>所有入口的終點：決定這一次活多久、錨在哪、怎麼換上去，再畫出來。</summary>
    /// <remarks>
    /// 釘住時每一種來源都用那扇窗：指名的換掉釘住的那一份（原地浮上來），清單上的借用它，
    /// 第一次借用時把釘住的那一份收起來等清單結束還回去。沒釘住時換了名稱就換位置：舊位置縮回
    /// 膠囊，新位置再長出來。
    /// </remarks>
    private void Show(
        PreviewTrigger trigger,
        ITrackingSpan anchor,
        SqlPreviewSubject subject,
        SqlMetadataService? metadataService)
    {
        var mode = PreviewLifecycle.ModeFor(trigger, _pinned);
        var transition = PreviewTransition.None;
        if (_pinned)
        {
            if (mode == PreviewMode.Browse && _held is null && _presenter?.Subject is { } pinnedSubject && _anchor is { } pinnedAnchor)
            {
                _held = new HeldPreview(pinnedSubject, _presenter.Service, pinnedAnchor, _anchorText);
            }
            else if (mode == PreviewMode.Pinned)
            {
                _held = null;
            }

            transition = PreviewTransition.Swap;
        }
        else if (IsShowing && !IsSameAnchor(_anchor, anchor))
        {
            transition = PreviewTransition.Move;
        }

        _mode = mode;
        _anchor = anchor;
        _anchorText = anchor.GetSpan(anchor.TextBuffer.CurrentSnapshot).GetText();
        Present(subject, metadataService, transition);
    }

    /// <summary>
    /// 換上內容並顯示；指令碼宣告的先向名冊要資料行。
    /// </summary>
    /// <remarks>
    /// 所有入口（向右鍵、停夠久、停留提示的連結、Ctrl+F12、Ctrl＋點擊、清單換選取、借用結束）的
    /// 終點都是這裡，所以「換內容之前要先停掉什麼」「換的時候怎麼動」都只有這一份。
    /// </remarks>
    private void Present(SqlPreviewSubject subject, SqlMetadataService? metadataService, PreviewTransition transition)
    {
        if (EnsureSurface() is not { } surface || _presenter is not { } presenter)
        {
            return;
        }

        if (!ResolveDeclared(subject))
        {
            ShowNothingForSelection();
            return;
        }

        // 正在換位置，或這一次要換位置：先在舊位置收成寫著新名稱的膠囊，收完才換內容。
        if (_agent is { } agent && _anchor is { } anchor &&
            (agent.IsRelocating || transition == PreviewTransition.Move))
        {
            _pending = (subject, metadataService);
            surface.SetContentState(subject.Label, ready: false);
            agent.Relocate(anchor, OnRelocated);
            return;
        }

        _pending = null;
        var changed = presenter.Show(subject, metadataService);
        surface.SetContentState(subject.Label, presenter.IsReady);
        if (changed && transition == PreviewTransition.Swap && IsShowing)
        {
            surface.PlaySwap();
        }

        ShowAgent();
    }

    /// <summary>舊位置已經縮回膠囊：這時才換上新內容，新位置長出來的就是它。</summary>
    private void OnRelocated()
    {
        if (_pending is not { } pending || _presenter is not { } presenter || _surface is not { } surface)
        {
            return;
        }

        _pending = null;
        presenter.Show(pending.Subject, pending.Service);
        surface.SetContentState(pending.Subject.Label, presenter.IsReady);

        // 錨點換了：滑鼠停留提示要讓位的名稱、字級與記住的尺寸都照新的一輪。
        ShowAgent();
    }

    /// <summary>
    /// 指令碼自己宣告的物件先向名冊要資料行；清單路過而讀不出來時回傳 false，不佔位置。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留與 Ctrl+F12 在定位那一步就把明細讀好了；建議清單那條入口只知道名稱，這裡才去問名冊。
    /// 使用者指名要看的讀不出來時照樣畫，由 <see cref="SqlStructurePresenter"/> 說出實情。
    ///
    /// 「清單路過」看的是這一份是不是清單選到的那一個，不看 <see cref="_mode"/>：借用期間路過
    /// 關鍵字時模式仍是 Browse，換回來的卻是釘住的那一份（<see cref="_held"/>）。看模式的話，
    /// 釘住的是讀不出來的宣告時它也被判成路過，<see cref="ShowNothingForSelection"/> 又把它
    /// 交回這裡，兩邊互相呼叫到堆疊溢位。
    /// </remarks>
    private bool ResolveDeclared(SqlPreviewSubject subject)
    {
        if (subject.Object is not { } objectInfo || !objectInfo.Kind.IsScriptDeclared())
        {
            return true;
        }

        if (subject.Script is null ||
            !SqlPreviewSubject.IsSameObject(subject.Script.Object, objectInfo))
        {
            subject.Script = FindDeclared(objectInfo.Name) is { } detail
                ? new SqlObjectStructure(detail)
                : null;
        }

        return subject.Script is not null || !ReferenceEquals(subject, _selection);
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

    /// <summary>
    /// 兩個錨點會不會把預覽擺在同一個地方。
    /// </summary>
    /// <remarks>
    /// 比起點不比整段：同一個名稱上先 Ctrl+F12、再從清單按向右鍵，清單的錨點長度可能不同，
    /// 預覽的落點卻一樣；那時縮回再長出來只是原地抖一下。
    /// </remarks>
    private static bool IsSameAnchor(ITrackingSpan? current, ITrackingSpan next)
    {
        if (current is null || !ReferenceEquals(current.TextBuffer, next.TextBuffer))
        {
            return false;
        }

        var snapshot = next.TextBuffer.CurrentSnapshot;
        return current.GetStartPoint(snapshot).Position == next.GetStartPoint(snapshot).Position;
    }

    /// <summary>
    /// 圖釘：釘住眼前這一份；放開回到指名，照游標規則收。
    /// </summary>
    /// <remarks>
    /// 借用中放開時窗回到清單旁邊繼續跟著選取，釘住的那一份跟著放掉；規則見
    /// <see cref="PreviewLifecycle.TogglePin"/>。
    /// </remarks>
    private void TogglePin()
    {
        if (_mode == PreviewMode.Hidden)
        {
            return;
        }

        (_mode, _pinned) = PreviewLifecycle.TogglePin(_mode, _pinned);
        _held = null;
        if (_mode == PreviewMode.Named && _anchor is { } anchor)
        {
            // 釘住期間名稱可能被改過；放開時以眼前的文字為準，不因為舊的差異立刻收掉。
            _anchorText = anchor.GetSpan(anchor.TextBuffer.CurrentSnapshot).GetText();
        }
        else if (_mode == PreviewMode.Browse && _session is { IsDismissed: false } session)
        {
            _anchor = session.ApplicableToSpan;
        }

        _surface?.SetPinned(_pinned);

        // 釘住就從眼前的位置開始自由擺放；放開回到錨點上下，錨點已經捲出畫面的話跟著收。
        if (_agent is { } agent)
        {
            UpdateAgentPreferences(agent);
            agent.RequestReposition();
        }

        if (_mode == PreviewMode.Browse)
        {
            ShowSelection();
        }
    }

    /// <summary>清單借用結束：釘住的窗換回釘住的那一份。</summary>
    private void ReturnToPin()
    {
        if (_held is not { } held)
        {
            Close(restoreEditorFocus: false);
            return;
        }

        _held = null;
        _mode = PreviewMode.Pinned;
        _anchor = held.Anchor;
        _anchorText = held.AnchorText;
        Present(held.Subject, held.Service, PreviewTransition.Swap);
    }

    /// <summary>收掉預覽並放下展開意圖；本來就沒顯示時回傳 false。</summary>
    private bool Close(bool restoreEditorFocus)
    {
        _expandTimer.Stop();
        _expandWhenSelectionReady = false;
        _mode = PreviewMode.Hidden;
        _pinned = false;
        _held = null;
        _pending = null;
        _anchorText = null;
        _presenter?.Clear();
        _surface?.SetPinned(false);
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
                            SqlSuggestionTarget.Describe(
                                suggestion,
                                SqlAsyncCompletionSource.StatementCandidatesOf(session)),
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

    private PreviewSurface? EnsureSurface()
    {
        if (_closed)
        {
            return null;
        }

        if (_surface is not null)
        {
            return _surface;
        }

        return SqlAssistPlatformGuard.Create("建立結構預覽", () =>
        {
            var panel = new SqlStructurePanel(_view);
            var surface = new PreviewSurface(panel);
            surface.DragStarted += OnDragStarted;
            surface.DragDelta += OnDragDelta;
            surface.DragCompleted += OnDragCompleted;
            surface.SizeResetRequested += OnSizeResetRequested;
            surface.CloseRequested += OnCloseRequested;
            surface.PinToggled += OnPinToggled;
            surface.DockRequested += OnDockRequested;
            _presenter = new SqlStructurePresenter(
                panel,
                _view.VisualElement.Dispatcher,
                NotificationOrigin.Typing,
                () => ActiveSqlEditor.GetDocumentName(_view));
            _presenter.Ready += OnPresenterReady;
            _surface = surface;
            return surface;
        });
    }

    /// <summary>查詢回來了：還停在膠囊的話照停留規則攤開。</summary>
    private void OnPresenterReady(object? sender, EventArgs eventArgs)
    {
        if (_presenter?.Subject is { } subject)
        {
            _surface?.SetContentState(subject.Label, ready: true);
        }
    }

    /// <summary>
    /// 移到工具視窗：眼前這一份交給停靠的視窗，浮動預覽收起。
    /// </summary>
    /// <remarks>
    /// 要一直開著、跨分頁、放到別的螢幕的，是停靠視窗的事；浮動預覽屬於這個編輯器，換分頁就藏起來。
    /// </remarks>
    private void OnDockRequested(object? sender, EventArgs eventArgs)
    {
        if (_presenter?.Subject is not { } subject)
        {
            return;
        }

        var service = _presenter.Service;
        Close(restoreEditorFocus: false);
        SqlStructureToolWindow.Show(_serviceProvider, subject, service);
    }

    private void OnCloseRequested(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("關閉結構預覽", () => Apply(PreviewSignal.Dismiss));
    }

    private void OnPinToggled(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("釘住結構預覽", TogglePin);
    }

    /// <summary>
    /// 開始拖抬頭或握把；拖抬頭就是要把它放到別處，還沒釘住的先釘住。
    /// </summary>
    /// <remarks>
    /// 錨在名稱上的視窗位置由錨點決定，搬過去放開又會被定位拉回來；使用者要的是「放在這裡」，
    /// 那正是釘住的意思。圖釘同時亮起來，看得出它現在不會自己收。
    /// </remarks>
    private void OnDragStarted(object sender, PreviewDragEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("開始拖曳結構預覽", () =>
        {
            if (_agent is not { } agent)
            {
                return;
            }

            if (eventArgs.Handle == PreviewDragHandle.Move && !_pinned)
            {
                TogglePin();
            }

            _dragStartWidth = agent.CurrentWidth;
            _dragStartHeight = agent.CurrentHeight;
            agent.BeginDrag(eventArgs.Handle);
        });
    }

    private void OnDragDelta(object sender, PreviewDragEventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run(
            "拖曳結構預覽",
            () => _agent?.Drag(eventArgs.HorizontalChange, eventArgs.VerticalChange));
    }

    /// <summary>
    /// 放開：錨在名稱上時把拖出來的尺寸記下來，釘住的只屬於那一扇窗。
    /// </summary>
    /// <remarks>
    /// 一軸位移不到一個握把的邊長就當作沒拖那一軸：角落握把一定同時動到兩軸，只想拉寬的人
    /// 也會順手帶進幾個像素的垂直位移。那幾個像素寫回去，放不下而被壓矮的高度就成了記住的
    /// 高度；寬度原本是「延伸到編輯器右側」的話，更會換成一個固定值再也回不去。
    ///
    /// 真的拖過的軸則照畫面上的值記，不管那一軸原本是不是被版面壓縮過——以前壓縮過的軸一律
    /// 不記，結果是下方空間不夠時怎麼拉高度，放開都彈回原樣。
    /// </remarks>
    private void OnDragCompleted(object sender, PreviewDragEventArgs eventArgs)
    {
        if (_agent is not { } agent)
        {
            return;
        }

        SqlAssistPlatformGuard.Run("儲存結構預覽尺寸", () =>
        {
            agent.CompleteDrag(eventArgs.Canceled);
            if (eventArgs.Canceled || agent.IsPinned)
            {
                return;
            }

            var widthChanged = Math.Abs(agent.CurrentWidth - _dragStartWidth) >= PreviewSurface.GripSize;
            var heightChanged = Math.Abs(agent.CurrentHeight - _dragStartHeight) >= PreviewSurface.GripSize;
            if (!widthChanged && !heightChanged)
            {
                return;
            }

            PreviewWindowState.Save(
                widthChanged ? agent.CurrentWidth : (double?)null,
                heightChanged ? agent.CurrentHeight : (double?)null);
            UpdateAgentPreferences(agent);
        });
    }

    private void OnSizeResetRequested(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("重設結構預覽尺寸", () =>
        {
            PreviewWindowState.Reset();
            if (_agent is { } agent)
            {
                UpdateAgentPreferences(agent);
                agent.ResizePinned(PreviewWindowState.Preferred);
                agent.RequestReposition();
            }
        });
    }

    /// <summary>錨點捲出畫面；定位途中發出，排到派送佇列之後再問生命週期。</summary>
    private void OnAnchorScrolledOut(object sender, EventArgs eventArgs)
    {
        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() => SqlAssistPlatformGuard.Run("錨點捲出畫面", () =>
            {
                // 排隊期間可能又捲回來、換了一個預覽或被釘住；只處理還成立的那一個。
                if (!_closed && _agent is { } agent && ReferenceEquals(agent, sender) && agent.IsAnchorOutOfView)
                {
                    Apply(PreviewSignal.AnchorScrolledOut);
                }
            })));
    }

    /// <summary>把自訂 Agent 掛上 reservation stack；已掛著時只更新狀態並重排。</summary>
    private void ShowAgent()
    {
        // 清單上展開的錨點跟著清單走：使用者繼續打字時 ApplicableToSpan 會跟著長。
        if (_mode == PreviewMode.Browse && _session is { IsDismissed: false } session)
        {
            _anchor = session.ApplicableToSpan;
        }

        if (_surface is not { } surface || _anchor is not { } anchor || _view.IsClosed)
        {
            return;
        }

        SqlAssistPlatformGuard.Run("顯示結構預覽", () =>
        {
            surface.Panel.ApplyFontSize(SqlAssistSettingsStore.Current.PreviewFontSize);

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
            surface.CompleteExit();

            // 重新出現就是另一次在看：上一次的搜尋字留著只會讓人以為那張表少了幾欄。
            // 切分頁回來、清單上路過關鍵字而暫時藏起來的不算，它們沒有換掉承載視窗。
            surface.Panel.ClearSearch();
            surface.SetPinned(_pinned);

            var created = new SqlPreviewPopupAgent(_view, _manager, anchor, surface);
            created.AnchorScrolledOut += OnAnchorScrolledOut;
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

    /// <summary>
    /// 把錨點、記住的尺寸與窗釘不釘住交給定位層。
    /// </summary>
    /// <remarks>
    /// 窗擺在哪只看 <see cref="_pinned"/>，不看 <see cref="_mode"/>：後者說的是內容活多久。
    /// 以前傳 <c>_mode == Pinned</c>，清單一借用釘住的窗（模式換成 Browse）定位層就當成放開圖釘，
    /// 窗跑到清單旁邊跟著走、圖釘卻還亮著，拖到別處放開又被拉回清單旁；清單結束還回去時，
    /// 釘住的位置已經換成清單旁邊那一格。
    /// </remarks>
    private void UpdateAgentPreferences(SqlPreviewPopupAgent agent)
    {
        if (_anchor is not { } anchor)
        {
            return;
        }

        agent.Update(anchor, PreviewWindowState.Preferred, _pinned);
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
            _surface?.Panel is not { } panel ||
            !panel.HasSelection())
        {
            return false;
        }

        panel.CopySelection();
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

        // 名冊抓著那個版本的整份文字，視窗都關了不必再留著。
        _declarationsSnapshot = null;
        _declarations = null;
        // 先放下狀態再放開清單：編輯器已經關了，沒有東西要縮回錨點。
        _mode = PreviewMode.Hidden;
        _pinned = false;
        _held = null;
        _pending = null;
        _shownAnchor = null;
        ReleaseSession(PreviewSignal.SessionEnded);
        SetObservedSession(null);
        ReleaseSurface();

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
        ReleaseSurface();
    }

    private void ReleaseSurface()
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

        if (_presenter is { } presenter)
        {
            presenter.Ready -= OnPresenterReady;
            presenter.Dispose();
            _presenter = null;
        }

        if (_surface is { } surface)
        {
            surface.DragStarted -= OnDragStarted;
            surface.DragDelta -= OnDragDelta;
            surface.DragCompleted -= OnDragCompleted;
            surface.SizeResetRequested -= OnSizeResetRequested;
            surface.CloseRequested -= OnCloseRequested;
            surface.PinToggled -= OnPinToggled;
            surface.DockRequested -= OnDockRequested;
            surface.Dispose();
            _surface = null;
        }
    }

    /// <summary>換內容時外形怎麼動。</summary>
    private enum PreviewTransition
    {
        /// <summary>不動：跟著清單選取換的，方向鍵連按時每一格都動一下只會拖慢眼睛。</summary>
        None,

        /// <summary>釘住的窗原地換：外形不動，新內容浮上來。</summary>
        Swap,

        /// <summary>換到別的名稱旁邊：舊位置縮回膠囊，新位置長出來。</summary>
        Move
    }

    /// <summary>清單借用釘住的窗時，被借走的那一份；清單結束原樣還回去。</summary>
    private sealed class HeldPreview
    {
        public HeldPreview(SqlPreviewSubject subject, SqlMetadataService? service, ITrackingSpan anchor, string? anchorText)
        {
            Subject = subject;
            Service = service;
            Anchor = anchor;
            AnchorText = anchorText;
        }

        public SqlPreviewSubject Subject { get; }

        public SqlMetadataService? Service { get; }

        /// <summary>釘住那一份原本的名稱；還回去之後放開圖釘，照它的游標規則收。</summary>
        public ITrackingSpan Anchor { get; }

        public string? AnchorText { get; }
    }
}
