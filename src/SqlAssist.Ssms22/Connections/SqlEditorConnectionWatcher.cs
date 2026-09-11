using System;
using System.Collections.Generic;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.Connections;

/// <summary>
/// 由 SSMS 的事件告訴每一份中繼資料服務「你那個查詢視窗換連線了」。
/// </summary>
/// <remarks>
/// <c>ISqlEditorService</c> 的 <c>ConnectionChanged</c> 由
/// <c>SqlScriptEditorControl.UpdateUIForNewCurrentDatabase</c> 觸發，而它的三個
/// 呼叫者正好涵蓋三種換法：執行完一批查詢（<c>USE</c> 走這條）、工具列的資料庫
/// 下拉選單、以及變更連線。<c>ConnectionDisconnected</c> 則由中斷連線觸發。
/// 少了這個出處，每多一種換法就要在別處補一次特別處理，而漏掉的那一種會安靜地
/// 用舊資料庫的物件回答。
///
/// 這裡只寫旗標。真的去問 SSMS「現在連到哪裡」留在既有的背景路徑上——那個呼叫
/// 有 UI 執行緒相依性，塞住時實測 1908 ms，而事件本身就在 UI 執行緒上。
/// 收到之後由 <see cref="SqlMetadataService.NoteConnectionChanged"/> 併進既有的
/// <c>IsConfirmationDue</c>，不另立一套判斷。
/// </remarks>
internal static class SqlEditorConnectionWatcher
{
    private static readonly object SyncRoot = new();
    private static readonly List<SqlMetadataService> Services = new();
    private static IServiceProvider? _serviceProvider;
    private static ISqlEditorService? _editorService;
    private static bool _subscribeStarted;
    private static bool _subscribed;
    private static bool _shutdown;

    /// <summary>
    /// 讓一個查詢視窗的中繼資料服務收得到連線事件。
    /// </summary>
    /// <remarks>
    /// 事件是全域服務的，服務卻是每個 <c>ITextView</c> 一份，兩者靠
    /// <c>EditorMoniker</c> 對應：焦點落在某個編輯器上時，那一刻的
    /// <c>GetActiveEditorMoniker()</c> 就是它的識別。不自己從文件路徑推——推錯的
    /// 症狀是事件永遠對不上任何一個視窗，而畫面上看不出差別。
    /// </remarks>
    public static void Attach(ITextView textView, SqlMetadataService service, IServiceProvider serviceProvider)
    {
        void OnGotFocus(object sender, EventArgs eventArgs) => CaptureMoniker(textView, service);

        void OnClosed(object sender, EventArgs eventArgs)
        {
            textView.GotAggregateFocus -= OnGotFocus;
            textView.Closed -= OnClosed;
            Unregister(service);
        }

        lock (SyncRoot)
        {
            if (_shutdown)
            {
                return;
            }

            Services.Add(service);
        }

        textView.GotAggregateFocus += OnGotFocus;
        textView.Closed += OnClosed;

        // 剛建立的查詢視窗通常還沒拿到焦點，第一次識別多半由 OnGotFocus 取得；
        // 已經有焦點時（服務比編輯器晚建立）就不必等下一次切換。
        CaptureMoniker(textView, service);

        EnsureSubscribed(serviceProvider);
    }

    /// <summary>視窗關掉或服務釋放時解除對應；兩邊都呼叫，重複呼叫無害。</summary>
    public static void Unregister(SqlMetadataService service)
    {
        lock (SyncRoot)
        {
            Services.Remove(service);
        }
    }

    /// <summary>套件卸載時退訂。</summary>
    public static void Shutdown()
    {
        ISqlEditorService? editorService;

        lock (SyncRoot)
        {
            _shutdown = true;
            Services.Clear();
            editorService = _subscribed ? _editorService : null;
            _subscribed = false;
            _editorService = null;
            _serviceProvider = null;
        }

        if (editorService is null)
        {
            return;
        }

        editorService.ConnectionChanged -= OnConnectionChanged;
        editorService.ConnectionDisconnected -= OnConnectionDisconnected;
    }

    /// <remarks>
    /// 訂閱要在 UI 執行緒上做，而第一份中繼資料服務不保證建立在那裡；
    /// MEF 的補全元件也不等 <c>AsyncPackage</c>，所以接線跟著服務走而不是跟著
    /// 套件初始化走——接晚了的症狀是使用者開 SSMS 之後的第一次換資料庫沒有人收到。
    /// </remarks>
    private static void EnsureSubscribed(IServiceProvider serviceProvider)
    {
        lock (SyncRoot)
        {
            if (_subscribeStarted || _shutdown)
            {
                return;
            }

            _subscribeStarted = true;
            _serviceProvider = serviceProvider;
        }

        SqlAssistPlatformGuard.BeginProbe("接上 SSMS 連線變更事件", async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Subscribe();
        });
    }

    private static void Subscribe()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ResolveEditorService() is not { } editorService)
        {
            // 訂閱不上不代表功能壞掉，而是退回十秒一次的背景輪詢與 USE 的文字訊號；
            // 那是一整族症狀的根源，所以一律留下紀錄。
            SqlAssistDiagnostics.WriteAlways("取不到 SSMS 查詢編輯器服務，連線變更改由背景輪詢發現");
            return;
        }

        lock (SyncRoot)
        {
            if (_shutdown || _subscribed)
            {
                return;
            }

            _subscribed = true;
        }

        editorService.ConnectionChanged += OnConnectionChanged;
        editorService.ConnectionDisconnected += OnConnectionDisconnected;
        SqlAssistDiagnostics.WriteAlways("已接上 SSMS 的查詢視窗連線變更事件");
    }

    private static void OnConnectionChanged(object sender, SqlEditorConnectionEventArgs eventArgs) =>
        Notify("連線變更", eventArgs);

    private static void OnConnectionDisconnected(object sender, SqlEditorConnectionEventArgs eventArgs) =>
        Notify("連線中斷", eventArgs);

    /// <remarks>
    /// 事件處理常式跑在 UI 執行緒上，丟出例外就是使用者眼前的錯誤對話框。
    /// </remarks>
    private static void Notify(string reason, SqlEditorConnectionEventArgs? eventArgs)
    {
        SqlAssistPlatformGuard.Run($"處理 SSMS {reason}", () =>
        {
            var moniker = eventArgs?.EditorMoniker;

            // 使用者換一次連線才一行，而「哪一種換法有沒有觸發事件」只有這裡看得出來。
            SqlAssistDiagnostics.WriteAlways(
                $"SSMS {reason}：{(string.IsNullOrEmpty(moniker) ? "（未指名視窗）" : moniker)}");

            SqlMetadataService[] services;

            lock (SyncRoot)
            {
                services = Services.ToArray();
            }

            // 通知在鎖外面發出：服務那一端會拿自己的鎖，而它釋放時又會回頭呼叫
            // Unregister，兩邊反向持鎖就是死結。
            foreach (var service in services)
            {
                service.NoteConnectionChanged(moniker);
            }
        });
    }

    /// <remarks>
    /// 排到派送佇列尾端再問，而且只在這個編輯器真的還握著焦點時才收下：焦點事件與
    /// SSMS 自己更新「目前是哪一個編輯器」沒有保證的先後，在原地問會拿到上一個
    /// 視窗的識別——那比沒有識別更糟，這一份服務會就此一路用別人的連線回答。
    ///
    /// 取不到就維持沒有識別，也就是退回問作用中視窗的舊行為，所以走 Probe。
    /// </remarks>
    private static void CaptureMoniker(ITextView textView, SqlMetadataService service)
    {
        TextViewDispatch.AfterCurrentCommand(textView, "取得查詢視窗識別", view =>
        {
            if (!view.HasAggregateFocus)
            {
                return;
            }

            SqlAssistPlatformGuard.Probe("取得查詢視窗識別", () =>
            {
                if (ResolveEditorService() is { } editorService)
                {
                    service.NoteEditorMoniker(editorService.GetActiveEditorMoniker());
                }
            });
        });
    }

    private static ISqlEditorService? ResolveEditorService()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_editorService is { } cached)
        {
            return cached;
        }

        // 先問傳進來的服務提供者，再退回全域服務——與 SSMS 自己的 ServiceCache
        // 同一個順序，第一支在殼層還沒把 SqlAssist 完全 site 好時會落空。
        var provider = _serviceProvider;
        _editorService = provider?.GetService(typeof(SSqlEditorService)) as ISqlEditorService
            ?? Package.GetGlobalService(typeof(SSqlEditorService)) as ISqlEditorService;
        return _editorService;
    }
}
