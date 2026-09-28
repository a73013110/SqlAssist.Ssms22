using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SqlAssist.Core.Notifications;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Connections;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 把一個主體畫進 <see cref="SqlStructurePanel"/>：分層取快取、節流之後才查、過期的結果不蓋掉眼前的。
/// </summary>
/// <remarks>
/// 浮動預覽與停靠的工具視窗畫的是同一種東西、走同一條分層載入。這段各寫一份的話，
/// 「命中第二層先畫欄位」「換了物件的查詢結果不得回寫」這類規則遲早只修到其中一邊。
///
/// 物件由便宜到昂貴依序嘗試：第四層快取命中就直接畫完；只有第二層命中就先畫欄位，
/// 索引與外來鍵稍後補上；兩層都沒有就先畫標題，等節流計時器到期才查資料庫。
/// 使用者按著方向鍵一路往下時，中途的每一項都不會送出查詢。
/// </remarks>
internal sealed class SqlStructurePresenter : IDisposable
{
    /// <summary>
    /// 換了物件之後多久才真的去查資料庫。
    /// </summary>
    /// <remarks>
    /// 用方向鍵連續移動時，每一格都送出一次查詢是純浪費——停下來的那一格才是
    /// 使用者要看的。這是實作細節而不是偏好，所以不開放設定。
    /// </remarks>
    private const int QueryDebounceMilliseconds = 150;

    private readonly SqlStructurePanel _panel;
    private readonly Dispatcher _dispatcher;
    private readonly NotificationOrigin _origin;
    private readonly Func<string> _documentName;
    private readonly DispatcherTimer _queryTimer;
    private CancellationTokenSource? _loading;

    /// <summary>換內容或停下時遞增；過期的查詢與節流不得越代更新。</summary>
    private long _generation;

    private long _queryGeneration;

    /// <param name="origin">浮動預覽是打字途中順手看的（Typing），工具視窗是使用者要的（User）。</param>
    /// <param name="documentName">通知上寫的「在哪裡發生」：浮動預覽是查詢視窗，工具視窗是它自己。</param>
    public SqlStructurePresenter(
        SqlStructurePanel panel,
        Dispatcher dispatcher,
        NotificationOrigin origin,
        Func<string> documentName)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _origin = origin;
        _documentName = documentName ?? throw new ArgumentNullException(nameof(documentName));
        _queryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(QueryDebounceMilliseconds)
        };
        _queryTimer.Tick += OnQueryTimerTick;
    }

    /// <summary>畫面上正在顯示的東西。</summary>
    public SqlPreviewSubject? Subject { get; private set; }

    public SqlMetadataService? Service { get; private set; }

    /// <summary>畫得出內容了，不只是標題與「載入中」。</summary>
    public bool IsReady { get; private set; }

    /// <summary>內容從「只有標題」變成畫得出來；載入成功或失敗都算，失敗也是一句要讓人看到的話。</summary>
    public event EventHandler? Ready;

    /// <summary>把主體畫出來；同一個主體與服務時不重畫，回傳是否換了內容。</summary>
    /// <param name="service">
    /// 內建說明不需要，傳 null；物件在對帳完成前也可能還沒有，那時只畫得出標題。
    /// </param>
    public bool Show(SqlPreviewSubject subject, SqlMetadataService? service)
    {
        if (subject is null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        if (ReferenceEquals(Subject, subject) && ReferenceEquals(Service, service))
        {
            // 畫面已經是這個東西了。重畫等於使用者眼前閃一下；換代還會取消掉剛送出
            // 的查詢，然後再等一次節流重送。
            return false;
        }

        Stop();
        Subject = subject;
        Service = service;
        IsReady = Draw(subject, service);
        return true;
    }

    /// <summary>停掉查詢並忘掉眼前的主體；畫面上的內容留著，收起的動畫還看得到它。</summary>
    public void Clear()
    {
        Stop();
        Subject = null;
        Service = null;
        IsReady = false;
    }

    public void Dispose()
    {
        Clear();
        _queryTimer.Tick -= OnQueryTimerTick;
        _loading?.Dispose();
        _loading = null;
    }

    private void Stop()
    {
        _generation++;
        _queryTimer.Stop();
        _loading?.Cancel();
    }

    /// <summary>畫出手上已有的；回傳內容是否已經畫得出來。</summary>
    private bool Draw(SqlPreviewSubject subject, SqlMetadataService? service)
    {
        // 內建說明是隨組件發布的一份資料：查表就有，畫完就結束，不起節流計時器。
        if (subject.BuiltIn is { } doc)
        {
            _panel.ShowBuiltIn(doc);
            return true;
        }

        if (subject.Object is not { } objectInfo)
        {
            return false;
        }

        // 指令碼自己宣告的物件不必經過任何一層快取或查詢：答案就在使用者眼前的文字裡，
        // 呼叫端在定位那一步就讀好了。
        if (objectInfo.Kind.IsScriptDeclared())
        {
            if (subject.Script is { } declared)
            {
                _panel.Populate(declared);
            }
            else
            {
                // 名稱認得出來、資料行讀不出來——SELECT * INTO #Loan FROM dbo.Loan 的欄位只有
                // 中繼資料知道，而這條路徑不等查詢。說出實情，不要畫一個空的結構讓人以為它真的沒有欄位。
                _panel.ShowMessage(objectInfo.QualifiedName, PreviewText.ScriptDeclaredNoColumns);
            }

            return true;
        }

        // 對帳還沒把中繼資料服務交過來就先展開了：先把標題畫出來，等它補上。
        if (service is null)
        {
            _panel.SetTarget(objectInfo);
            return false;
        }

        if (service.PeekStructure(objectInfo) is { } structure)
        {
            _panel.Populate(structure);
            return true;
        }

        _panel.SetTarget(objectInfo);
        var detail = service.PeekDetail(objectInfo);
        if (detail is not null)
        {
            _panel.PopulatePartial(detail);
        }

        _queryGeneration = _generation;
        _queryTimer.Start();

        // 第二層的欄位已經是答案的大半，索引與外來鍵由抬頭的「載入中」交代，不必讓人對著膠囊等。
        return detail is not null;
    }

    private void OnQueryTimerTick(object sender, EventArgs eventArgs)
    {
        _queryTimer.Stop();

        if (_queryGeneration == _generation &&
            Subject is { Object: { } target } &&
            Service is { } service)
        {
            BeginLoad(target, service);
        }
    }

    private void BeginLoad(SqlObjectInfo objectInfo, SqlMetadataService service)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var source = new CancellationTokenSource();
        _loading = source;
        var generation = _generation;

        // 取消一律當成正常結束：換了物件或收起了視窗，什麼都不用做。
        SqlAssistPlatformGuard.Begin(
            NotificationCatalog.LoadingStructurePreview,
            () => LoadAsync(objectInfo, service, source, generation),
            NotificationKind.Preview, _origin, NotificationLevel.Info,
            _documentName(), objectInfo.QualifiedName);
    }

    private async Task LoadAsync(
        SqlObjectInfo objectInfo,
        SqlMetadataService service,
        CancellationTokenSource source,
        long generation)
    {
        var cancellationToken = source.Token;
        var structure = await service
            .GetStructureAsync(objectInfo, cancellationToken, _origin)
            .ConfigureAwait(false);

        await _dispatcher.InvokeAsync(
            () =>
            {
                // 等待期間使用者可能已經移到別的項目，那就不要蓋掉他正在看的東西。
                if (cancellationToken.IsCancellationRequested ||
                    generation != _generation ||
                    !ReferenceEquals(_loading, source) ||
                    !SqlPreviewSubject.IsSameObject(Subject?.Object, objectInfo) ||
                    !ReferenceEquals(Service, service))
                {
                    return;
                }

                if (structure is null)
                {
                    _panel.ShowMessage(objectInfo.QualifiedName, PreviewText.NoConnection);
                }
                else
                {
                    _panel.Populate(structure);
                }

                var wasReady = IsReady;
                IsReady = true;
                if (!wasReady)
                {
                    Ready?.Invoke(this, EventArgs.Empty);
                }
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }
}
