using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 手動清除紀錄的條件與試算；按下清除後回傳請求，實際刪除由用量頁執行並顯示進度。畫面在 <see cref="SqlMemoryCleanupView"/>。
/// </summary>
/// <remarks>
/// 每次改條件 250 ms 後在背景重新試算，按鈕寫出筆數：使用者按下去之前就知道會刪多少。
/// 試算是上限——共用內容與保護根在刪除交易內才逐筆重查——所以文案說「最多」。
/// 不再疊第二層確認框：這個對話框本身就是確認，筆數寫在按鈕上、取消是預設，條件一改清除就停用到新試算回來。
/// </remarks>
internal sealed class SqlMemoryCleanupWindow : DialogWindow
{
    private readonly SqlMemoryCleanupView _view;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource _pending = new();
    private SqlMemoryCleanupEstimate? _latest;
    private int _generation;

    private SqlMemoryCleanupWindow(SqlAssistPackage package)
    {
        SqlMemoryActions.ConfigureWindow(this, package, "清除 SQL Memory 紀錄", 560, 600);
        MinWidth = 480; MinHeight = 480;
        SizeToContent = SizeToContent.Height;

        var server = SqlConnectionTagInput.CreateInput();
        var database = SqlConnectionTagInput.CreateInput();
        _view = new SqlMemoryCleanupView(
            SqlConnectionTagInput.CreateBar(package, SqlIcon.Server, server, "伺服器", databases: false, () => null, includeFavorites: false, Report),
            server,
            SqlConnectionTagInput.CreateBar(package, SqlIcon.Database, database, "資料庫", databases: true,
                () => server.Text.Trim() is { Length: > 0 } text ? text : null, includeFavorites: false, Report),
            database) { Margin = SqlAssistChrome.DialogPadding };
        Content = _view;

        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SqlMemoryActions.RunAsync(EstimateAsync, Report); };
        _view.Changed += (_, _) => SqlMemoryActions.Run(Changed, Report);
        _view.Submit.Click += (_, _) => SqlMemoryActions.Run(Submit, Report);
        Closed += (_, _) => { _debounce.Stop(); _pending.Cancel(); _pending.Dispose(); };
        Changed();
    }

    public SqlMemoryCleanupRequest? Request { get; private set; }

    /// <returns>使用者確認的請求；取消為 null。</returns>
    public static SqlMemoryCleanupRequest? Show(SqlAssistPackage package)
    {
        var window = new SqlMemoryCleanupWindow(package);
        return window.ShowModal() == true ? window.Request : null;
    }

    private void Changed()
    {
        _latest = null;
        _generation++;
        _pending.Cancel(); _pending.Dispose(); _pending = new CancellationTokenSource();
        _debounce.Stop();
        var hasTargets = _view.Compose(DateTimeOffset.UtcNow) is not null;
        _view.ShowEstimating(hasTargets);
        if (hasTargets) _debounce.Start();
    }

    private async Task EstimateAsync()
    {
        if (_view.Compose(DateTimeOffset.UtcNow) is not { } request) return;
        var generation = _generation;
        var token = _pending.Token;
        try
        {
            var estimate = await SqlMemoryHost.Runtime.EstimateCleanupAsync(request, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            _latest = estimate;
            _view.ShowEstimate(estimate);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (generation == _generation) _view.ShowEstimateFailure(SqlMemoryTimeText.Failure("試算", error));
        }
    }

    private void Submit()
    {
        if (_latest is not { Total: > 0 } || _view.Compose(DateTimeOffset.UtcNow) is not { } request) return;
        Request = request;
        DialogResult = true;
    }

    private void Report(string message) => _view.Report(message);
}
