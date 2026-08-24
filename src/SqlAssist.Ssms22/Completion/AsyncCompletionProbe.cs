using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SqlAssist.Ssms22.Completion;

/// <summary>
/// 記錄新版非同步 IntelliSense（Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion）
/// 在 SSMS 22 的 SQL 編輯器裡究竟有沒有被接上。
/// </summary>
/// <remarks>
/// SSMS 的 T-SQL IntelliSense 是舊版語言服務，官方文件沒有說明新版 API 是否對
/// ContentType "SQL" 生效。整個建議清單要不要改用平台原生的 async completion，
/// 取決於這份探測結果，因此先量測再決定架構，不要憑猜測改寫。
/// </remarks>
internal static class AsyncCompletionProbe
{
    private static readonly object SyncRoot = new();
    private static int _providerRequested;
    private static int _initializeCalled;
    private static int _participated;
    private static int _contextRequested;
    private static int _descriptionRequested;
    private static int _lastItemCount;
    private static string _brokerSupport = "尚未量測";
    private static string _lastTrigger = "尚未觸發";
    private static string _lastError = "無";

    /// <summary>編輯器是否認為此 ContentType 支援新版非同步完成。</summary>
    public static string BrokerSupport
    {
        get
        {
            lock (SyncRoot)
            {
                return _brokerSupport;
            }
        }
    }

    public static int ProviderRequested => Volatile.Read(ref _providerRequested);

    public static int InitializeCalled => Volatile.Read(ref _initializeCalled);

    public static int Participated => Volatile.Read(ref _participated);

    public static int ContextRequested => Volatile.Read(ref _contextRequested);

    public static int DescriptionRequested => Volatile.Read(ref _descriptionRequested);

    public static void RecordBrokerSupport(string contentTypeName, bool supported)
    {
        lock (SyncRoot)
        {
            _brokerSupport = string.Format(
                CultureInfo.InvariantCulture,
                "{0} → {1}",
                contentTypeName,
                supported ? "支援" : "不支援");
        }
    }

    /// <summary>SSMS 完全沒有匯出 broker，代表新版 API 在這個宿主上不可用。</summary>
    public static void RecordBrokerMissing()
    {
        lock (SyncRoot)
        {
            _brokerSupport = "SSMS 未匯出 IAsyncCompletionBroker";
        }
    }

    public static void RecordBrokerFailure(Exception exception)
    {
        lock (SyncRoot)
        {
            _brokerSupport = $"查詢失敗：{exception.Message}";
        }
    }

    public static void RecordProviderRequested()
    {
        if (Interlocked.Increment(ref _providerRequested) == 1)
        {
            SqlAssistDiagnostics.WriteAlways("探測：平台已索取非同步建議來源，Provider 有被掃描到");
        }
    }

    public static void RecordInitialize(string triggerDescription)
    {
        var count = Interlocked.Increment(ref _initializeCalled);

        lock (SyncRoot)
        {
            _lastTrigger = $"{DateTimeOffset.Now:HH:mm:ss} {triggerDescription}";
        }

        // 這是決定架構的關鍵事實：平台是否真的把按鍵路由進非同步完成管線。
        // 只記第一次，避免每個按鍵都寫一行。
        if (count == 1)
        {
            SqlAssistDiagnostics.WriteAlways(
                $"探測：InitializeCompletion 首次被呼叫，非同步完成管線在 SQL 編輯器有效（觸發：{triggerDescription}）");
        }
    }

    public static void RecordParticipation()
    {
        Interlocked.Increment(ref _participated);
    }

    public static void RecordContext(int itemCount)
    {
        Interlocked.Increment(ref _contextRequested);
        Volatile.Write(ref _lastItemCount, itemCount);
    }

    public static void RecordDescription()
    {
        Interlocked.Increment(ref _descriptionRequested);
    }

    public static void RecordError(Exception exception)
    {
        lock (SyncRoot)
        {
            _lastError = $"{DateTimeOffset.Now:HH:mm:ss} {exception.GetType().Name}：{exception.Message}";
        }
    }

    /// <summary>組出可直接貼進診斷對話框的摘要。</summary>
    public static string BuildReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Broker 支援狀態：{BrokerSupport}");
        builder.AppendLine($"Provider 被索取次數：{ProviderRequested}");
        builder.AppendLine($"InitializeCompletion 呼叫次數：{InitializeCalled}");
        builder.AppendLine($"實際參與完成次數：{Participated}");
        builder.AppendLine($"GetCompletionContext 呼叫次數：{ContextRequested}");
        builder.AppendLine($"最後一次提供項目數：{Volatile.Read(ref _lastItemCount)}");
        builder.AppendLine($"GetDescription 呼叫次數：{DescriptionRequested}");

        lock (SyncRoot)
        {
            builder.AppendLine($"最後觸發：{_lastTrigger}");
            builder.AppendLine($"最後錯誤：{_lastError}");
        }

        return builder.ToString();
    }
}
