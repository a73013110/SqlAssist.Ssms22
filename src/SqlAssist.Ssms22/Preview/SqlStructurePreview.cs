using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 編輯器上的浮動結構預覽。
/// </summary>
/// <remarks>
/// 用的是編輯器自己的空間保留機制，也就是 IntelliSense 清單與提示視窗用的那一套。
/// 這帶來三件單靠 WPF <c>Popup</c> 做不到的事：
/// 位置由平台計算，會自動避開已經佔位的建議清單並在撞到邊界時翻到另一側；
/// 焦點落在視窗裡時編輯器仍然算「持有焦點」，所以用滑鼠拉選文字不會把建議清單關掉；
/// 編輯器捲動或關閉時，視窗跟著走、跟著收。
/// </remarks>
internal sealed class SqlStructurePreview
{
    /// <summary>視窗最多佔掉編輯器的多少比例，免得整個查詢視窗被蓋住。</summary>
    private const double MaximumViewportRatio = 0.8;

    private readonly IWpfTextView _view;
    private readonly IServiceProvider _serviceProvider;
    private readonly DispatcherTimer _timer;

    private SqlStructurePreviewControl? _control;
    private ISpaceReservationManager? _manager;
    private ISpaceReservationAgent? _agent;

    /// <summary>
    /// 交給平台的那一層容器。
    /// </summary>
    /// <remarks>
    /// 內容控制項是重複使用的，但每一次顯示都會產生一個新的代理人，
    /// 而代理人會把交給它的元素掛進自己的 <c>Popup</c>。
    /// 同一個 WPF 元素不能同時有兩個父代，因此中間隔一層可拋棄的容器：
    /// 換代理人時先把內容從舊容器取下，再放進新的。
    /// </remarks>
    private System.Windows.Controls.Decorator? _host;
    private ITrackingSpan? _anchor;
    private IAsyncCompletionSession? _session;
    private SqlObjectInfo? _target;
    private SqlMetadataService? _metadataService;
    private CancellationTokenSource? _loading;
    private bool _closed;

    /// <summary>計時器到期時要做的是「自動展開」而不是「去查資料庫」。</summary>
    private bool _timerExpands;

    /// <summary>正在自己換掉代理人，這一次移除通知不是平台在收視窗。</summary>
    private bool _recreatingAgent;

    private bool _activationAttached;

    private SqlStructurePreview(IWpfTextView view, IServiceProvider serviceProvider)
    {
        _view = view;
        _serviceProvider = serviceProvider;

        _timer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher);
        _timer.Tick += OnTimerTick;

        view.Closed += OnViewClosed;
    }

    /// <summary>
    /// 交給平台的擺放樣式。
    /// </summary>
    /// <remarks>
    /// <see cref="SqlPreviewPlacement.Beside"/> 用
    /// <see cref="PopupStyles.PositionLeftOrRight"/>：穩定的「優先右側、撞邊才翻」。
    /// 刻意不加 <see cref="PopupStyles.PositionClosest"/>，那會讓平台每次都挑
    /// 「當下比較近的一邊」，於是視窗一變寬就跳到另一側，拖曳握把時看起來
    /// 像是左邊界在往外長。
    ///
    /// <see cref="SqlPreviewPlacement.Stacked"/> 則什麼旗標都不給，那正是平台的
    /// 預設行為——擺在錨點所在行的下方，下面放不下才翻到上方。
    ///
    /// 兩者都刻意不加任何 <c>DismissOnMouseLeave</c>：預覽的生死由這個類別自己管，
    /// 滑鼠移開就消失的視窗沒辦法讓人把裡面的文字拉選起來。
    /// </remarks>
    private static PopupStyles Styles => Placement == SqlPreviewPlacement.Stacked
        ? PopupStyles.None
        : PopupStyles.PositionLeftOrRight;

    private static SqlPreviewPlacement Placement =>
        SettingsService.Default.GetSnapshot().Preview.Placement;

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
        if (_closed || _control is not null)
        {
            return;
        }

        _view.VisualElement.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                try
                {
                    EnsureControl();
                }
                catch (Exception exception)
                {
                    SqlAssistDiagnostics.WriteAlways($"預先建立結構預覽失敗：{exception}");
                }
            }));
    }

    /// <summary>記住目前的建議清單，並在它結束時把預覽收掉。</summary>
    public void TrackSession(IAsyncCompletionSession session)
    {
        if (_closed || session is null)
        {
            return;
        }

        _session = session;
        _anchor = session.ApplicableToSpan;
        session.Dismissed += OnSessionEnded;
        session.ItemCommitted += OnSessionItemCommitted;
    }

    private void OnSessionItemCommitted(object sender, EventArgs eventArgs) => EndSession();

    private void OnSessionEnded(object sender, EventArgs eventArgs) => EndSession();

    private void EndSession()
    {
        Invoke(() =>
        {
            if (_session is { } session)
            {
                session.Dismissed -= OnSessionEnded;
                session.ItemCommitted -= OnSessionItemCommitted;
                _session = null;
            }

            // 挑選結束就收起來——展開狀態不跨越 session，
            // 下一次開清單又是從乾淨的畫面開始。
            IsExpanded = false;
            Hide();
        });
    }

    /// <summary>
    /// 建議清單的選取換了一項。
    /// </summary>
    /// <remarks>
    /// 沒有展開就只記住是誰，什麼都不畫也不查——使用者用方向鍵掃過二十項時，
    /// 這裡會被呼叫二十次。
    /// </remarks>
    public void OnItemSelected(SqlObjectInfo? objectInfo, SqlMetadataService metadataService)
    {
        if (_closed)
        {
            return;
        }

        Invoke(() =>
        {
            _metadataService = metadataService;
            _target = objectInfo;

            if (IsExpanded)
            {
                if (objectInfo is null)
                {
                    _timer.Stop();
                    _loading?.Cancel();
                    EnsureControl()?.ShowMessage("沒有結構可以顯示", "這一項不是資料庫物件。");
                    return;
                }

                ShowTarget(objectInfo, metadataService);
                return;
            }

            var settings = SettingsService.Default.GetSnapshot().Preview;

            if (settings.Mode != SqlPreviewMode.Delay || objectInfo is null)
            {
                return;
            }

            // 延遲模式：停在同一項夠久才展開。掃過去的那幾項連查詢都不會送出。
            _timer.Stop();
            _timerExpands = true;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, settings.ClampDelay()));
            _timer.Start();
        });
    }

    /// <summary>展開預覽；已經展開時回傳 false，讓按鍵照原本的方式往下走。</summary>
    public bool Expand()
    {
        if (_closed || IsExpanded)
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
        else
        {
            EnsureControl()?.ShowMessage("沒有結構可以顯示", "這一項不是資料庫物件。");
            ShowAgent();
        }

        return true;
    }

    /// <summary>收合預覽；本來就沒展開時回傳 false。</summary>
    public bool Collapse()
    {
        if (!IsExpanded)
        {
            return false;
        }

        IsExpanded = false;
        Hide();
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
            _anchor = anchor;
            _target = objectInfo;
            _metadataService = metadataService;
            IsExpanded = true;
            ShowTarget(objectInfo, metadataService);
        });
    }

    /// <summary>收掉視窗；本來就沒顯示時回傳 false。</summary>
    public bool Hide()
    {
        _timer.Stop();
        _timerExpands = false;
        _loading?.Cancel();

        if (_agent is null || _manager is null)
        {
            return false;
        }

        try
        {
            var hadFocus = _agent.HasFocus;
            _manager.RemoveAgent(_agent);

            // 焦點在預覽裡時直接移除，鍵盤會落到不明的地方；還給編輯器。
            if (hadFocus && !_view.IsClosed)
            {
                _view.VisualElement.Focus();
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"收起結構預覽失敗：{exception.Message}");
        }
        finally
        {
            _agent = null;
        }

        return true;
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

        var delay = SettingsService.Default.GetSnapshot().Preview.ClampDelay();

        if (delay == 0)
        {
            BeginLoad(objectInfo, metadataService);
            return;
        }

        _timer.Interval = TimeSpan.FromMilliseconds(delay);
        _timer.Start();
    }

    private void OnTimerTick(object sender, EventArgs eventArgs)
    {
        _timer.Stop();

        if (_timerExpands)
        {
            _timerExpands = false;
            Run(() => Expand());
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
        _ = LoadAsync(objectInfo, metadataService, source.Token);
    }

    private async Task LoadAsync(
        SqlObjectInfo objectInfo,
        SqlMetadataService metadataService,
        CancellationToken cancellationToken)
    {
        try
        {
            var structure = await metadataService
                .GetStructureAsync(objectInfo, cancellationToken)
                .ConfigureAwait(false);

            await _view.VisualElement.Dispatcher.InvokeAsync(
                () =>
                {
                    // 等待期間使用者可能已經移到別的項目，那就不要蓋掉他正在看的東西。
                    if (cancellationToken.IsCancellationRequested ||
                        _target?.ObjectId != objectInfo.ObjectId ||
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
        catch (OperationCanceledException)
        {
            // 換了物件或收起了視窗，什麼都不用做。
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"載入結構預覽失敗：{exception}");
        }
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

        try
        {
            var settings = SettingsService.Default.GetSnapshot().Preview;
            var control = new SqlStructurePreviewControl
            {
                PreferredWidth = settings.ClampWidth(),
                PreferredHeight = settings.ClampHeight()
            };

            control.SizeCommitted += OnSizeCommitted;
            control.CloseRequested += OnCloseRequested;
            _control = control;
            return control;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"建立結構預覽失敗：{exception}");
            return null;
        }
    }

    private void OnCloseRequested(object sender, EventArgs eventArgs)
    {
        IsExpanded = false;
        Hide();
    }

    private void OnSizeCommitted(object sender, EventArgs eventArgs)
    {
        if (_control is not { } control)
        {
            return;
        }

        try
        {
            SettingsService.Default.Update(settings =>
            {
                // 上下擺放的寬度是編輯器決定的，不是使用者拖出來的；
                // 寫回去等於用視窗寬度蓋掉他為側邊擺放調好的那一個值。
                if (Placement != SqlPreviewPlacement.Stacked)
                {
                    settings.Preview.Width = control.PreferredWidth;
                }

                settings.Preview.Height = control.PreferredHeight;
            });

            // 放開手才收斂一次位置：拖曳期間重排等於在使用者手上把視窗抽來抽去。
            if (_agent is not null && _manager is not null && _anchor is not null)
            {
                _manager.UpdatePopupAgent(_agent, _anchor, Styles);
                UpdateGripSide();
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"儲存結構預覽尺寸失敗：{exception.Message}");
        }
    }

    /// <summary>把視窗掛上編輯器；已經掛著就只更新錨點。</summary>
    private void ShowAgent()
    {
        if (_control is not { } control || _anchor is null || _view.IsClosed)
        {
            return;
        }

        try
        {
            if (_manager is null)
            {
                _manager = _view.GetSpaceReservationManager(
                    SqlPreviewDefinitions.SpaceReservationManagerName);

                if (_manager is null)
                {
                    return;
                }

                // 平台會在自己認為該收起來的時候移除代理人（例如編輯器失去聚合焦點），
                // 不通知的話這裡會一直握著一個已經死掉的代理人，
                // 下一次顯示就只會走「更新位置」而什麼都不做。
                _manager.AgentChanged += OnAgentChanged;
                AttachApplicationActivation();
            }

            ApplySize(control);

            if (_agent is not null)
            {
                _manager.UpdatePopupAgent(_agent, _anchor, Styles);
            }
            else
            {
                if (_host is not null)
                {
                    _host.Child = null;
                }

                _host = new System.Windows.Controls.Decorator { Child = control };
                _agent = _manager.CreatePopupAgent(_anchor, Styles, _host);
                _manager.AddAgent(_agent);

                // 只有真的新掛上去才淡入。更新位置也播的話，方向鍵每按一下
                // 整個視窗就閃一次，那不是動效而是雜訊。
                control.PlayAppear();
            }

            // 位置要等平台排完版才問得到，因此排在版面之後。
            _view.VisualElement.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(UpdateGripSide));
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"顯示結構預覽失敗：{exception}");
            _agent = null;
        }
    }

    /// <summary>
    /// 套用尺寸；上下擺放時寬度改由編輯器決定。
    /// </summary>
    /// <remarks>
    /// 橫幅的價值就在「一次攤開一百多個欄位而不必橫向捲動」，寬度沿用側邊擺放
    /// 那個 620 就沒有意義了。
    ///
    /// 高度則跟側邊擺放走同一條規則。原本另外壓成編輯器的 45%，理由是「擺在
    /// 程式碼上下的東西太高會遮掉太多行」——但那個上限比使用者拖出來的高度還低，
    /// 於是每次重新顯示都把他調好的尺寸壓回去，看起來就是「拖了不算數」。
    /// 要遮多少行是使用者自己的取捨，程式不該替他決定。
    /// </remarks>
    private void ApplySize(SqlStructurePreviewControl control)
    {
        var size = SettingsService.Default.GetSnapshot().Preview;
        var availableWidth = _view.ViewportWidth * MaximumViewportRatio;
        var availableHeight = _view.ViewportHeight * MaximumViewportRatio;

        if (Placement == SqlPreviewPlacement.Stacked)
        {
            control.ApplySize(
                _view.ViewportWidth,
                size.ClampHeight(),
                _view.ViewportWidth,
                availableHeight);
            return;
        }

        control.ApplySize(size.ClampWidth(), size.ClampHeight(), availableWidth, availableHeight);
    }

    /// <summary>
    /// 判斷視窗落在錨點的哪一側，並把縮放握把放到它實際會長大的那一角。
    /// </summary>
    /// <remarks>
    /// 貼在左側時平台釘住的是視窗的右邊界，加寬會往左長；握把留在右下角的話，
    /// 使用者往右拖曳卻看到左邊界往外跑。判斷不出來時一律當成右側，
    /// 那是絕大多數情況，也是預設的版面。
    ///
    /// 上下擺放不需要判斷：視窗跨滿整個編輯器寬度，左右兩邊都釘死了，
    /// 拿右邊界跟游標比較只會得到隨機的結果。
    /// </remarks>
    private void UpdateGripSide()
    {
        if (_control is not { } control || _host is null || _view.IsClosed)
        {
            return;
        }

        if (Placement == SqlPreviewPlacement.Stacked)
        {
            control.SetWidthLocked(true);
            control.SetGripSide(onLeft: false);
            return;
        }

        control.SetWidthLocked(false);

        try
        {
            var popupRight = _host.PointToScreen(new Point(_host.ActualWidth, 0)).X;
            var caretX = _view.Caret.Left - _view.ViewportLeft;
            var anchorScreenX = _view.VisualElement.PointToScreen(new Point(caretX, 0)).X;
            control.SetGripSide(popupRight <= anchorScreenX);
        }
        catch (Exception exception)
        {
            // 版面還沒完成時 PointToScreen 會失敗，那就維持現狀。
            SqlAssistDiagnostics.Write($"判斷結構預覽的位置失敗：{exception.Message}");
        }
    }

    /// <summary>
    /// 切換到別的應用程式再回來時，把視窗重新掛一次。
    /// </summary>
    /// <remarks>
    /// 浮動視窗是自己的一個承載視窗，SSMS 失去啟用狀態再取回時，它的輸入狀態會留在
    /// 舊的狀態上——表現出來就是「怎麼拉都選不起來，必須先點回查詢視窗再點預覽」。
    /// 整個換一個新的承載視窗最省事，內容控制項本來就是重複使用的，成本很低。
    /// </remarks>
    private void AttachApplicationActivation()
    {
        if (_activationAttached || System.Windows.Application.Current is not { } application)
        {
            return;
        }

        _activationAttached = true;
        application.Activated += OnApplicationActivated;
    }

    private void OnApplicationActivated(object sender, EventArgs eventArgs)
    {
        if (_closed || _agent is null || _manager is null || _anchor is null)
        {
            return;
        }

        Run(() =>
        {
            try
            {
                _recreatingAgent = true;
                _manager.RemoveAgent(_agent);
                _agent = null;
            }
            finally
            {
                _recreatingAgent = false;
            }

            ShowAgent();
        });
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
        if (_agent is null || _control is not { } control || !control.HasSelection())
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
        if (_recreatingAgent || _agent is null || !ReferenceEquals(eventArgs.OldAgent, _agent))
        {
            return;
        }

        _agent = eventArgs.NewAgent;

        if (_agent is null)
        {
            IsExpanded = false;
            _timer.Stop();
            _timerExpands = false;
            _loading?.Cancel();
        }
    }

    /// <summary>確保工作落在 UI 執行緒上；已經在上面就直接執行，不多繞一圈。</summary>
    private void Invoke(Action action)
    {
        var dispatcher = _view.VisualElement.Dispatcher;

        if (dispatcher.CheckAccess())
        {
            Run(action);
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Run(action)));
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            // 這些工作都掛在按鍵與滑鼠路徑上，例外冒出去就是一個錯誤對話框。
            SqlAssistDiagnostics.WriteAlways($"結構預覽操作失敗：{exception}");
        }
    }

    private void OnViewClosed(object sender, EventArgs eventArgs)
    {
        _closed = true;
        _view.Closed -= OnViewClosed;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _loading?.Cancel();
        EndSession();

        if (_control is { } control)
        {
            control.SizeCommitted -= OnSizeCommitted;
            control.CloseRequested -= OnCloseRequested;
            _control = null;
        }

        if (_manager is { } manager)
        {
            manager.AgentChanged -= OnAgentChanged;
            _manager = null;
        }

        if (_activationAttached && System.Windows.Application.Current is { } application)
        {
            application.Activated -= OnApplicationActivated;
            _activationAttached = false;
        }

        _agent = null;
    }
}
