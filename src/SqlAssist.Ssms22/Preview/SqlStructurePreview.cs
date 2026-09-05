using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
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
    private readonly DispatcherTimer _timer;

    private SqlStructurePreviewControl? _control;
    private ISpaceReservationManager? _manager;
    private SqlPreviewPopupAgent? _agent;
    private ITrackingSpan? _anchor;
    private IAsyncCompletionSession? _observedSession;
    private IAsyncCompletionSession? _session;
    private SqlObjectInfo? _target;
    private SqlMetadataService? _metadataService;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _selectionRefresh;
    private bool _closed;

    /// <summary>計時器到期時要做的是「自動展開」而不是「去查資料庫」。</summary>
    private bool _timerExpands;

    private bool _layoutUpdateQueued;

    private bool _selectedItemIsSqlObject;

    private bool _selectionPending;

    /// <summary>選取尚在背景對帳時收到向右鍵，驗證成功後替使用者完成展開。</summary>
    private bool _expandWhenSelectionReady;

    private bool _inputTrackingAttached;

    /// <summary>已經排了一次預先建立；建議清單每開一次都會呼叫 <see cref="Warmup"/>。</summary>
    private bool _warmupQueued;

    /// <summary>每次換 session、選取、獨立入口或收合都遞增；過期 timer/load 不得越代更新。</summary>
    private long _generation;

    private long _timerGeneration;

    private double _resizeStartWidth;

    private double _resizeStartHeight;

    /// <summary>拖曳開始時這一軸就已經是版面壓縮的結果；壓縮值不得寫回偏好尺寸。</summary>
    private bool _resizeStartWidthConstrained;

    private bool _resizeStartHeightConstrained;

    private SqlStructurePreview(IWpfTextView view, IServiceProvider serviceProvider)
    {
        _view = view;
        _serviceProvider = serviceProvider;

        _timer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher);
        _timer.Tick += OnTimerTick;

        view.Closed += OnViewClosed;
        view.LayoutChanged += OnViewLayoutChanged;
        view.ViewportLeftChanged += OnViewportGeometryChanged;
        view.ViewportWidthChanged += OnViewportGeometryChanged;
        view.ViewportHeightChanged += OnViewportGeometryChanged;
        view.ZoomLevelChanged += OnZoomLevelChanged;
    }

    private static SqlPreviewPlacement Placement =>
        SqlAssistSettingsStore.Current.PreviewPlacement;

    /// <summary>預覽目前是否展開；建議清單的方向鍵處理需要知道。</summary>
    public bool IsExpanded { get; private set; }

    /// <summary>目前是不是由建議清單驅動；沒有清單時 Esc 才該由預覽自己吃掉。</summary>
    public bool HasSession => _session is not null;

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
                current.Dismissed -= OnSessionEnded;
                current.ItemCommitted -= OnSessionItemCommitted;
                current.ItemsUpdated -= OnSessionItemsUpdated;
                _view.TextBuffer.Changed -= OnTextBufferChanged;
                _session = null;
                _target = null;
                _metadataService = null;
                _selectedItemIsSqlObject = false;
                _selectionPending = false;
                _expandWhenSelectionReady = false;
                DetachInputTracking();
                _generation++;
                IsExpanded = false;
                Hide(restoreEditorFocus: false);
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
                    _metadataService = metadataService;
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

        if (_selectedItemIsSqlObject)
        {
            return Expand();
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
    private void TrackSession(IAsyncCompletionSession session)
    {
        if (_closed || session is null || session.IsDismissed)
        {
            return;
        }

        if (ReferenceEquals(_session, session))
        {
            _anchor = session.ApplicableToSpan;
            return;
        }

        if (_session is { } previous && !ReferenceEquals(previous, session))
        {
            previous.Dismissed -= OnSessionEnded;
            previous.ItemCommitted -= OnSessionItemCommitted;
            previous.ItemsUpdated -= OnSessionItemsUpdated;
            _view.TextBuffer.Changed -= OnTextBufferChanged;
        }

        _generation++;
        StopPendingWork();
        if (IsExpanded)
        {
            IsExpanded = false;
            Hide(restoreEditorFocus: false);
        }

        if (_observedSession is { } observed)
        {
            observed.Dismissed -= OnObservedSessionEnded;
        }

        _session = session;
        _observedSession = session;
        _anchor = session.ApplicableToSpan;
        _target = null;
        _metadataService = null;
        _selectedItemIsSqlObject = false;
        _selectionPending = false;
        _expandWhenSelectionReady = false;
        session.Dismissed += OnSessionEnded;
        session.ItemCommitted += OnSessionItemCommitted;
        session.ItemsUpdated += OnSessionItemsUpdated;
        _view.TextBuffer.Changed += OnTextBufferChanged;
        AttachInputTracking();
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
        if (_session is not { } session)
        {
            return;
        }

        // 涵蓋輸入、Backspace、貼上與復原；等平台更新篩選後再於背景讀最新選取。
        Invoke(() => BeginReconcile(session, cancelExpandIntent: true));
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

            session.Dismissed -= OnSessionEnded;
            session.ItemCommitted -= OnSessionItemCommitted;
            session.ItemsUpdated -= OnSessionItemsUpdated;
            _view.TextBuffer.Changed -= OnTextBufferChanged;
            _session = null;
            if (ReferenceEquals(_observedSession, session))
            {
                _observedSession = null;
            }
            _target = null;
            _metadataService = null;
            _selectedItemIsSqlObject = false;
            _selectionPending = false;
            _expandWhenSelectionReady = false;
            DetachInputTracking();
            _generation++;

            // 挑選結束就收起來——展開狀態不跨越 session，
            // 下一次開清單又是從乾淨的畫面開始。
            IsExpanded = false;
            Hide(restoreEditorFocus: false);
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

            _metadataService = metadataService;
            BeginReconcile(session, cancelExpandIntent: false);
        });
    }

    /// <summary>只套用已由 generation 與 recent model 驗證過的項目；必須在 UI 執行緒。</summary>
    private void ApplyVerifiedSelection(
        IAsyncCompletionSession session,
        SqlObjectInfo? objectInfo,
        SqlMetadataService metadataService)
    {
        if (_closed || session.IsDismissed || !ReferenceEquals(_session, session))
        {
            return;
        }

        var expandWhenReady = _expandWhenSelectionReady;
        var settings = SqlAssistSettingsStore.Current;
        _selectionPending = false;
        _expandWhenSelectionReady = false;
        _selectedItemIsSqlObject = objectInfo is not null;

        // 平台一次換選取會通知好幾輪，多數輪次解析出來的是同一個物件。
        // 展開狀態下解析出 null 是例外：那時畫面上可能還停在「正在取得目前建議項目…」，
        // 得讓它走下去換成正式訊息。
        var sameContent = IsSameObject(_target, objectInfo) &&
                          ReferenceEquals(_metadataService, metadataService) &&
                          (!IsExpanded || objectInfo is not null);

        if (!sameContent)
        {
            _generation++;
            StopPendingWork();
            _metadataService = metadataService;
            _target = objectInfo;
        }

        if (expandWhenReady &&
            !IsExpanded &&
            objectInfo is not null &&
            settings.Enabled &&
            settings.PreviewMode == SqlPreviewMode.RightArrow)
        {
            Expand();
            return;
        }

        if (IsExpanded)
        {
            if (sameContent)
            {
                // 畫面已經是這個物件了。重畫等於使用者眼前閃一下；換代還會取消掉剛送出
                // 的查詢，然後再等一次節流重送。
                return;
            }

            if (objectInfo is null)
            {
                EnsureControl()?.ShowMessage("沒有結構可以顯示", "這一項不是資料庫物件。");
                return;
            }

            ShowTarget(objectInfo, metadataService);
            return;
        }

        if (!settings.Enabled ||
            settings.PreviewMode != SqlPreviewMode.Delay ||
            objectInfo is null)
        {
            return;
        }

        // 延遲模式：停在同一項夠久才展開。掃過去的那幾項連查詢都不會送出。
        // 同一項的重複通知也走到這裡，倒數因此重新起算——代價是多等一次對帳的幾毫秒，
        // 換到的是「按了方向鍵就一定重新計時」這個使用者真正在感覺的規則。
        _timerExpands = true;
        _timerGeneration = _generation;
        _timer.Interval = TimeSpan.FromMilliseconds(
            Math.Max(MinimumExpandDelayMilliseconds, settings.PreviewDelayMilliseconds));
        _timer.Start();
    }

    /// <summary>
    /// 兩次對帳指的是不是同一個資料庫物件。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="SqlObjectInfo.ObjectId"/> 而不是參考相等：中繼資料快取、
    /// 詳細資料與結構查詢從頭到尾都以 ObjectId 當識別，這裡跟著同一套才不會出現
    /// 「快取認為是同一個、預覽認為換人了」。參考相等目前剛好成立，但那只是因為
    /// 兩次都讀到同一個 CompletionItem，換一條入口（停留提示、工具選單）就不成立。
    /// </remarks>
    private static bool IsSameObject(SqlObjectInfo? left, SqlObjectInfo? right) =>
        left is null
            ? right is null
            : right is not null && left.ObjectId == right.ObjectId;

    /// <summary>展開預覽；已經展開時回傳 false，讓按鍵照原本的方式往下走。</summary>
    public bool Expand()
    {
        var settings = SqlAssistSettingsStore.Current;
        if (_closed || IsExpanded || !settings.Enabled || settings.PreviewMode == SqlPreviewMode.Off)
        {
            return false;
        }

        IsExpanded = true;

        if (_session is { } session)
        {
            _anchor = session.ApplicableToSpan;
        }

        if (_target is { } target && _metadataService is { } metadataService)
        {
            ShowTarget(target, metadataService);
        }
        else if (_target is { } pendingTarget)
        {
            EnsureControl()?.SetTarget(pendingTarget);
            ShowAgent();
        }
        else
        {
            EnsureControl()?.ShowMessage(
                "結構預覽",
                _session is null ? "沒有結構可以顯示。" : "正在取得目前建議項目…");
            ShowAgent();
        }

        return true;
    }

    /// <summary>收合預覽；本來就沒展開時回傳 false。</summary>
    public bool Collapse()
    {
        if (!IsExpanded)
        {
            if (_expandWhenSelectionReady)
            {
                // 向右鍵尚在等背景對帳時，向左鍵代表取消這次展開意圖。
                _expandWhenSelectionReady = false;
                return true;
            }

            return false;
        }

        _expandWhenSelectionReady = false;
        IsExpanded = false;
        Hide(restoreEditorFocus: false);
        return true;
    }

    /// <summary>
    /// 直接顯示某個物件的結構，錨在指定的範圍上。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留提示的連結與工具選單的命令走這條路。與建議清單共用同一個視窗、
    /// 同一份資料路徑，差別只在錨點與誰負責收掉它。
    /// </remarks>
    public void ShowAt(ITrackingSpan anchor, SqlObjectInfo objectInfo, SqlMetadataService metadataService)
    {
        if (_closed || objectInfo is null)
        {
            return;
        }

        Invoke(() =>
        {
            DetachSession();
            _generation++;
            StopPendingWork();
            _anchor = anchor;
            _target = objectInfo;
            _metadataService = metadataService;
            IsExpanded = true;
            ShowTarget(objectInfo, metadataService);
        });
    }

    /// <summary>收掉視窗；本來就沒顯示時回傳 false。</summary>
    public bool Hide() => Hide(restoreEditorFocus: false);

    private bool Hide(bool restoreEditorFocus)
    {
        _generation++;
        StopPendingWork();

        if (_agent is not { } agent || _manager is not { } manager)
        {
            return false;
        }

        SqlAssistPlatformGuard.Run("收起結構預覽", () =>
        {
            var hadFocus = agent.HasFocus;
            var removed = manager.RemoveAgent(agent);
            if (!removed)
            {
                // Manager 已先移除時仍要確定關掉 HWND，不留下孤兒 Popup。
                agent.Dispose();
                if (ReferenceEquals(_agent, agent))
                {
                    _agent = null;
                }
            }

            // 焦點在預覽裡時直接移除，鍵盤會落到不明的地方；還給編輯器。
            // 只有使用者從預覽主動關閉才還焦點；Alt+Tab／session 結束不能搶回 SSMS。
            if (restoreEditorFocus && hadFocus && !_view.IsClosed && _view.VisualElement.IsVisible)
            {
                _view.VisualElement.Focus();
            }
        });

        return true;
    }

    private void StopPendingWork()
    {
        _timer.Stop();
        _timerExpands = false;
        _loading?.Cancel();
        _selectionRefresh?.Cancel();
    }

    private void DetachSession()
    {
        if (_session is not { } session)
        {
            SetObservedSession(null);
            return;
        }

        session.Dismissed -= OnSessionEnded;
        session.ItemCommitted -= OnSessionItemCommitted;
        session.ItemsUpdated -= OnSessionItemsUpdated;
        _view.TextBuffer.Changed -= OnTextBufferChanged;
        _session = null;
        _selectedItemIsSqlObject = false;
        _selectionPending = false;
        _expandWhenSelectionReady = false;
        DetachInputTracking();
        if (ReferenceEquals(_observedSession, session))
        {
            _observedSession = null;
        }
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
    /// 刻意不動 <see cref="_target"/>、節流計時器與載入工作，也不碰畫面。平台換一次選取
    /// 會從方向鍵命令、說明 callback 與 <c>ItemsUpdated</c> 分別通知一次；只要其中一條
    /// 先把畫面清成「正在取得目前建議項目…」，另外兩條就會讓同一個物件再重畫一次——
    /// 使用者看到的是每按一次方向鍵閃一下，而且剛送出的查詢會被取消再送一次。
    /// 該不該換內容留給 <see cref="ApplyVerifiedSelection"/>，只有它知道新舊是不是同一個。
    ///
    /// 舊物件留在畫面上不會被誤用：這裡把 <see cref="_selectedItemIsSqlObject"/> 壓成
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

        _selectedItemIsSqlObject = false;
        _selectionPending = true;

        // 「停夠久才自動展開」的倒數前提是使用者停在同一項上，換了就重新起算——
        // 不取消的話，倒數會在對帳完成前到期，於是展開的是上一項。
        // 節流計時器與進行中的查詢不在此列：它們跟的是畫面上的物件，而畫面沒變。
        if (_timerExpands)
        {
            _timer.Stop();
            _timerExpands = false;
        }

        QueueSelectionRefresh(session);
    }

    /// <summary>對帳結果確定不是資料庫物件；這時才真的丟掉目標並換掉畫面。</summary>
    private void ClearSelection(IAsyncCompletionSession session)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        _generation++;
        StopPendingWork();
        _target = null;
        _selectedItemIsSqlObject = false;
        _selectionPending = false;
        _expandWhenSelectionReady = false;
        if (IsExpanded)
        {
            EnsureControl()?.ShowMessage(
                "沒有結構可以顯示",
                "目前選取的項目不是資料庫物件。");
        }
    }

    /// <summary>
    /// 等平台先處理完這次鍵盤／滑鼠輸入，再從背景取得最新選取。
    /// </summary>
    /// <remarks>
    /// <see cref="IAsyncCompletionSession.GetComputedItems"/> 可能等待正在執行的篩選，
    /// 絕不能放在按鍵的 UI 執行緒。背景等待同時補足 ItemsUpdated 不會為單純上下移動
    /// 觸發的缺口，也讓「點回同一項」不會永遠停在失效狀態。
    /// </remarks>
    private void QueueSelectionRefresh(IAsyncCompletionSession session)
    {
        _selectionRefresh?.Cancel();
        var source = new CancellationTokenSource();
        _selectionRefresh = source;
        var generation = _generation;

        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (source.IsCancellationRequested ||
                    generation != _generation ||
                    !ReferenceEquals(_selectionRefresh, source) ||
                    !ReferenceEquals(_session, session) ||
                    session.IsDismissed)
                {
                    if (ReferenceEquals(_selectionRefresh, source))
                    {
                        _selectionRefresh = null;
                    }

                    source.Dispose();
                    return;
                }

                SqlAssistPlatformGuard.Begin(
                    "取得最新的建議選取",
                    () => RefreshSelectionAsync(session, source, generation));
            }));
    }

    private async Task RefreshSelectionAsync(
        IAsyncCompletionSession session,
        CancellationTokenSource source,
        long generation)
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
                    if (source.IsCancellationRequested ||
                        generation != _generation ||
                        !ReferenceEquals(_selectionRefresh, source) ||
                        !ReferenceEquals(_session, session) ||
                        session.IsDismissed)
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
                        _metadataService is { } metadataService)
                    {
                        // 只有此處同時驗證過 source、generation 與 recent model，才可更新 target。
                        ApplyVerifiedSelection(session, suggestion.Tag as SqlObjectInfo, metadataService);
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
    /// 載入並顯示一個物件。
    /// </summary>
    /// <remarks>
    /// 由便宜到昂貴依序嘗試：第四層快取命中就直接畫完；只有第二層命中就先畫欄位，
    /// 索引與外來鍵稍後補上；兩層都沒有就先畫標題，等節流計時器到期才查資料庫。
    /// 使用者按著方向鍵一路往下時，中途的每一項都不會送出查詢。
    /// </remarks>
    private void ShowTarget(SqlObjectInfo objectInfo, SqlMetadataService metadataService)
    {
        var control = EnsureControl();

        if (control is null)
        {
            return;
        }

        _timer.Stop();
        _timerExpands = false;
        _loading?.Cancel();

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

        _timerGeneration = _generation;
        _timer.Interval = TimeSpan.FromMilliseconds(QueryDebounceMilliseconds);
        _timer.Start();
    }

    private void OnTimerTick(object sender, EventArgs eventArgs)
    {
        _timer.Stop();

        if (_timerGeneration != _generation)
        {
            _timerExpands = false;
            return;
        }

        if (_timerExpands)
        {
            _timerExpands = false;
            var settings = SqlAssistSettingsStore.Current;
            if (settings.Enabled &&
                settings.PreviewMode == SqlPreviewMode.Delay &&
                _session is { IsDismissed: false } &&
                _target is not null)
            {
                SqlAssistPlatformGuard.Run("結構預覽操作", () => Expand());
            }

            return;
        }

        if (_target is { } target && _metadataService is { } metadataService && IsExpanded)
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
            "載入結構預覽",
            () => LoadAsync(objectInfo, metadataService, source, generation));
    }

    private async Task LoadAsync(
        SqlObjectInfo objectInfo,
        SqlMetadataService metadataService,
        CancellationTokenSource source,
        long generation)
    {
        var cancellationToken = source.Token;
        var structure = await metadataService
            .GetStructureAsync(objectInfo, cancellationToken)
            .ConfigureAwait(false);

        await _view.VisualElement.Dispatcher.InvokeAsync(
            () =>
            {
                // 等待期間使用者可能已經移到別的項目，那就不要蓋掉他正在看的東西。
                if (cancellationToken.IsCancellationRequested ||
                    generation != _generation ||
                    !ReferenceEquals(_loading, source) ||
                    !IsSameObject(_target, objectInfo) ||
                    !ReferenceEquals(_metadataService, metadataService) ||
                    _control is not { } control)
                {
                    return;
                }

                if (structure is null)
                {
                    control.ShowMessage(
                        objectInfo.QualifiedName,
                        "沒有可用的連線；請先在查詢視窗連上資料庫。");
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
            _control = control;
            return control;
        });
    }

    private void OnCloseRequested(object sender, EventArgs eventArgs)
    {
        SqlAssistPlatformGuard.Run("關閉結構預覽", () =>
        {
            _expandWhenSelectionReady = false;
            IsExpanded = false;
            Hide(restoreEditorFocus: true);
        });
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
        if (_session is { IsDismissed: false } session)
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
                return;
            }

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

            control.PlayAppear();
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

                    if (_session is { IsDismissed: false } session)
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
            _expandWhenSelectionReady = false;
            IsExpanded = false;
            _generation++;
            StopPendingWork();
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
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        _selectionRefresh?.Cancel();
        _selectionRefresh = null;
        DetachSession();

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
            control.Dispose();
            _control = null;
        }

        if (_manager is { } manager)
        {
            manager.AgentChanged -= OnAgentChanged;
            _manager = null;
        }

    }
}
