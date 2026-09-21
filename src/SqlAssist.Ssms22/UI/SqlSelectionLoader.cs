using System;
using System.Threading;
using System.Windows.Threading;

namespace SqlAssist.Ssms22.UI;

/// <summary>
/// 選取驅動的非同步讀取：去彈跳、取消上一輪，以及「回來的這一份還是不是目前選取的那一列」。
/// </summary>
/// <remarks>
/// SQL Memory 與 SQL Search 的 Preview 共用。兩邊<b>讀什麼</b>與<b>畫什麼</b>完全不同，
/// 所以共用的不是面板而是這一段時序——它才是兩邊原本各寫一次、而且已經開始分岔的東西
/// （其中一份的收尾少了一個取消檢查，兩邊都對，但只是碰巧）。
///
/// <b>選取變更一定要去彈跳加取消。</b>使用者用方向鍵在清單上捲過去時，每一列都發一輪查詢會把
/// 連線打滿，而那幾十輪裡他只看了最後一列。
///
/// <see cref="IsCurrent"/> 是整份契約的重點，成功與失敗<b>兩條路都要問</b>：舊請求的成功回應與
/// 舊請求的例外一樣，都不能蓋掉目前這一列。它同時檢查 token 與 <see cref="Current"/>——只比對
/// 列參考的那一版在「重新選同一列」時會判成還是目前這一列，於是上一輪的收尾把新那一輪剛點亮的
/// 載入狀態關掉，畫面上是一塊不再轉的空白。
///
/// 領域自己的作廢條件不在這裡：SQL Memory 還要比對儲存層的 generation 與可用狀態，
/// 那是它的事實，呼叫端在 <see cref="IsCurrent"/> 之外自己加。
/// </remarks>
/// <typeparam name="TRow">選取的那一列；以<b>參考</b>辨識，所以列物件不得在原地重建。</typeparam>
internal sealed class SqlSelectionLoader<TRow> : IDisposable where TRow : class
{
    private readonly DispatcherTimer _delay;
    private readonly Action<TRow, CancellationToken> _load;
    private CancellationTokenSource _read = new();
    private bool _disposed;

    /// <param name="load">
    /// 去彈跳之後要做的事；例外處理與平台防護留在呼叫端，這一層不決定失敗要寫到哪裡。
    /// </param>
    public SqlSelectionLoader(Dispatcher dispatcher, TimeSpan delay, Action<TRow, CancellationToken> load)
    {
        if (dispatcher is null) throw new ArgumentNullException(nameof(dispatcher));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _delay = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            if (_disposed || Current is not { } row) return;
            _load(row, _read.Token);
        };
    }

    /// <summary>目前選取的那一列；沒有選取時 null。</summary>
    public TRow? Current { get; private set; }

    /// <summary>
    /// 目前這一輪的取消權杖；列操作要跟著同一個選取的生命週期時取它。
    /// </summary>
    /// <remarks>
    /// 已經 <see cref="Dispose"/> 過就回一個已取消的權杖，而不是去碰已經釋放的來源：
    /// 面板關掉的那一刻還在飛的工作本來就該當成取消，讓呼叫端多接一個
    /// <see cref="ObjectDisposedException"/> 不會讓任何人知道得更多。
    /// </remarks>
    public CancellationToken Token => _disposed ? new CancellationToken(canceled: true) : _read.Token;

    /// <summary>
    /// 換一列。停掉還沒發出去的那一輪、取消已經發出去的那一輪，然後才換 <see cref="Current"/>。
    /// </summary>
    /// <remarks>
    /// 只換不讀：要不要讀由呼叫端看過這一列之後用 <see cref="Load"/> 決定——沒有定義可讀的來源
    /// 與收合起來的預覽都不該發查詢，而那個判斷需要領域知識。
    /// </remarks>
    public void Select(TRow? row)
    {
        if (_disposed) return;
        _delay.Stop();
        _read.Cancel();
        _read.Dispose();
        _read = new CancellationTokenSource();
        Current = row;
    }

    /// <summary>開始這一列的去彈跳；沒有選取就不排。</summary>
    public void Load()
    {
        if (_disposed || Current is null) return;
        _delay.Start();
    }

    /// <summary>這一輪回來的東西還能不能寫上畫面。</summary>
    public bool IsCurrent(TRow row, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && ReferenceEquals(row, Current);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _delay.Stop();
        Current = null;
        _read.Cancel();
        _read.Dispose();
    }
}
