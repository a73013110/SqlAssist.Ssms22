using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Updates;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Commands;

/// <summary>
/// 「檢查更新…」：問 GitHub 的最新發行版本；有新版跳出提醒，其餘結論是一列活動。
/// </summary>
/// <remarks>
/// 兩個入口（工具選單與「關於與診斷」的按鈕）與啟動時的自動檢查共用這一份實作；
/// 解析與比對在 Core 的 <see cref="SqlAssistUpdateCheck"/>，這裡只負責 HTTP、ETag 快取與通知。
///
/// <b>不下載也不安裝 VSIX。</b>安裝前要關掉所有 SSMS，擴充在自己的宿主裡做不完這件事；
/// 提醒的「前往下載」只把使用者送到那一版的發行頁，而且要使用者自己按。
///
/// 手動與自動的差別：自動的每天最多連網一次、只在有新版時出聲，並尊重「略過此版本」；
/// 手動的三種結論都回報，也不理會略過與「稍後」——使用者是自己按的。
/// </remarks>
internal static class SqlAssistUpdateCheckCommand
{
    /// <summary>連線加讀取的總時限；問不到就是問不到，不讓背景工作掛著。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly Lazy<HttpClient> Client = new(CreateClient);

    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>使用者自己按的：進度與「已是最新／查不到」是一列活動，有新版跳出提醒。</summary>
    public static void Execute(SqlAssistPackage package)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = ExecuteAsync(package);
    }

    /// <summary>
    /// 套件載入後的自動檢查；關掉設定或還在跑都直接跳過。
    /// </summary>
    /// <remarks>
    /// 今天問過了就不連網，直接拿快取的 tag 比對：比現在新、又不是被略過的那一版，照樣提醒。
    /// 只在「今天第一次啟動」提醒的版本，使用者當天重開 SSMS 就再也看不到。
    ///
    /// 連網那一條走 <see cref="SqlAssistPlatformGuard.BeginProbe"/>：離線時它會每次啟動都失敗一次，
    /// 那是連續失敗的探測，用 <c>Run</c> 記錄只會灌滿紀錄檔蓋掉真正的錯誤。
    /// </remarks>
    public static void ScheduleStartupCheck(SqlAssistPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!SqlAssistSettingsStore.Current.CheckForUpdates) return;
        if (DateTimeOffset.UtcNow - SqlAssistState.UpdateCheckedAt < SqlAssistUpdateCheck.AutomaticInterval)
        {
            AnnounceAutomatic(SqlAssistUpdateCheck.Compare(SqlAssistPackage.PackageVersion, SqlAssistState.UpdateTag));
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        SqlAssistPlatformGuard.BeginProbe("啟動時檢查更新", async () =>
        {
            try
            {
                var (result, etag) = await CheckAsync(package.DisposalToken).ConfigureAwait(false);
                await RememberAsync(result, etag, package.DisposalToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                AnnounceAutomatic(result);
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    /// <summary>「略過此版本」：記下那一版，自動檢查不再為它提醒。</summary>
    public static void SkipVersion(string version)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SqlAssistState.SkippedUpdateTag = SqlAssistUpdateCheck.ParseTag(version);
    }

    /// <summary>「前往下載」：開那一版的發行頁；參數不是 GitHub 的 https 網址時改開最新版的發行頁。</summary>
    public static void OpenReleasePage(string url)
    {
        var target = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                     uri.Host == "github.com"
            ? uri.AbsoluteUri
            : SqlAssistUpdateCheck.LatestReleasePageUrl;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>自動檢查的結論：只有新版、而且不是被略過的那一版才提醒；已是最新與查不到都不出聲。</summary>
    private static void AnnounceAutomatic(SqlAssistUpdateResult result)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!result.ShouldAnnounce || result.LatestVersion == SqlAssistState.SkippedUpdateTag) return;
        Prompt(result, NotificationOrigin.Ambient, NotificationLevel.Notice);
    }

    private static void Prompt(SqlAssistUpdateResult result, NotificationOrigin origin, NotificationLevel level) =>
        NotificationCenter.Default.Prompt(
            NotificationCatalog.UpdateAvailablePrompt(result.LatestVersion, SqlAssistUpdateCheck.ReleasePageUrl(result.LatestVersion)),
            NotificationKind.Update, origin, level);

    private static async Task ExecuteAsync(SqlAssistPackage package)
    {
        try
        {
            SqlAssistUpdateResult result;

            using (var notification = NotificationCenter.Default.Begin(NotificationCatalog.CheckingForUpdates,
                       NotificationKind.Update, NotificationOrigin.User, NotificationLevel.Info))
            {
                try
                {
                    var (checkResult, etag) = await CheckAsync(package.DisposalToken).ConfigureAwait(false);
                    result = checkResult;
                    await RememberAsync(result, etag, package.DisposalToken);

                    // 有新版時結論在提醒上，這一列只收尾，不再說一次同一句話。
                    if (!result.ShouldAnnounce) notification.Report(result.Message);

                    // 查不到不是成功：使用者按了之後要知道這一次沒有答案，而不是以為自己是最新版。
                    if (result.Status == SqlAssistUpdateStatus.Unknown) notification.Fail();
                }
                catch (OperationCanceledException) when (package.DisposalToken.IsCancellationRequested)
                {
                    notification.Cancel();
                    return;
                }
                catch (Exception error)
                {
                    SqlAssistDiagnostics.WriteAlways($"檢查更新失敗：{error}");
                    notification.Report(SqlAssistUpdateCheck.Unknown().Message);
                    notification.Fail();
                    return;
                }
            }

            // 使用者自己按的：不理會略過的版本，也不理會稍早按過的「稍後」。
            if (result.ShouldAnnounce) Prompt(result, NotificationOrigin.User, NotificationLevel.Info);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>
    /// 記下這一次的時間與 ETag 快取。
    /// </summary>
    /// <remarks>
    /// 狀態存放區只在 UI 執行緒讀寫，而回應是在執行緒集區上收到的。寫不進去的代價是
    /// 下一次啟動再問一次，所以失敗只記錄、不打斷結論。
    /// </remarks>
    private static async Task RememberAsync(SqlAssistUpdateResult result, string? etag, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        SqlAssistState.UpdateCheckedAt = DateTimeOffset.UtcNow;

        // 只在真的解析出版本時記下快取；否則下一次仍應該重問一份完整回應。
        if (etag is null || result.LatestVersion.Length == 0) return;
        SqlAssistState.UpdateTag = result.LatestVersion;
        SqlAssistState.UpdateETag = etag;
    }

    /// <summary>
    /// 問一次 GitHub；304 沿用上一次的 tag，其餘狀態碼一律當成「查不到」。
    /// </summary>
    /// <returns>結論，以及要記下來的 ETag；null 代表這一次不更新快取。</returns>
    private static async Task<(SqlAssistUpdateResult Result, string? ETag)> CheckAsync(CancellationToken cancellationToken)
    {
        // 存放區只在 UI 執行緒讀；呼叫端都是從那裡進來的，第一個 await 之前讀完。
        using var request = new HttpRequestMessage(HttpMethod.Get, SqlAssistUpdateCheck.LatestReleaseApiUrl);
        var etag = SqlAssistState.UpdateETag;
        var cachedTag = SqlAssistState.UpdateTag;

        // 帶著 ETag 再問，沒變就是 304：未驗證的配額是每小時 60 次，304 不吃完整回應。
        if (etag.Length > 0 && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);

        using var response = await Client.Value.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return (SqlAssistUpdateCheck.Compare(SqlAssistPackage.PackageVersion, cachedTag), null);

        if (!response.IsSuccessStatusCode)
        {
            SqlAssistDiagnostics.WriteAlways($"檢查更新：GitHub 回應 {(int)response.StatusCode}，這一次沒有答案。");
            return (SqlAssistUpdateCheck.Unknown(), null);
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return (SqlAssistUpdateCheck.FromReleaseJson(SqlAssistPackage.PackageVersion, body),
            response.Headers.ETag?.ToString() ?? string.Empty);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout };
        // GitHub 對沒有 User-Agent 的請求直接回 403，這個標頭是必要的。版本取不到時
        // （組件中繼資料缺漏，顯示成「未知」）不能直接塞進標頭，那不是合法的 token。
        var version = SqlAssistUpdateCheck.ParseTag(SqlAssistPackage.PackageVersion);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            SqlAssistUpdateCheck.UserAgentProduct, version.Length > 0 ? version : "0.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
