using System;
using System.Threading;
using System.Windows.Threading;

namespace SqlAssist.Ssms22.UI;

/// <summary>合併殼層及編輯器的連續外觀通知；排程不碰 WPF 元素，刷新才回 UI 執行緒。</summary>
internal sealed class ThemeRefreshQueue : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private Action? _refresh;
    private int _pending;

    public ThemeRefreshQueue(Dispatcher dispatcher, Action refresh)
    {
        _dispatcher = dispatcher;
        _refresh = refresh;
    }

    public void Request()
    {
        if (Volatile.Read(ref _refresh) is null || _dispatcher.HasShutdownStarted ||
            Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Volatile.Read(ref _refresh)?.Invoke();
        }));
    }

    public void Dispose()
    {
        // 待派送的工作不再抓住查詢視窗；即使關閉後才執行，也不會觸碰已釋放的內容。
        Interlocked.Exchange(ref _refresh, null);
    }
}
