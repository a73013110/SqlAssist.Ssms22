using System;
using SqlAssist.Core.Json;

namespace SqlAssist.Core.Updates;

/// <summary>檢查更新的三種結論。</summary>
public enum SqlAssistUpdateStatus
{
    /// <summary>問不到、或回來的內容看不出版本；不是失敗，只是這一次沒有答案。</summary>
    Unknown,

    UpToDate,

    UpdateAvailable,
}

/// <summary>一次檢查的結論與要顯示的那一句。</summary>
public sealed class SqlAssistUpdateResult
{
    internal SqlAssistUpdateResult(SqlAssistUpdateStatus status, string latestVersion, string message)
    {
        Status = status;
        LatestVersion = latestVersion;
        Message = message;
    }

    public SqlAssistUpdateStatus Status { get; }

    /// <summary>發行頁上的版本，例如 <c>0.16.3</c>；問不到時是空字串。</summary>
    public string LatestVersion { get; }

    /// <summary>通知卡片上的那一句；呼叫端不自己組字串。</summary>
    public string Message { get; }

    /// <summary>有新版才值得打斷使用者；啟動時的自動檢查只在這一種情況顯示卡片。</summary>
    public bool ShouldAnnounce => Status == SqlAssistUpdateStatus.UpdateAvailable;
}

/// <summary>
/// 比對 GitHub 的最新發行版本與目前這一版。
/// </summary>
/// <remarks>
/// 只做解析與比對：HTTP、ETag 快取、通知卡片與開瀏覽器都在 Ssms22 那一層。
/// 分開的理由是這裡的每一種結論都要測得到，而那些都需要網路與殼層。
///
/// 版本字串不正規化：<c>tools/Publish-Release.ps1</c> 打的 tag 是
/// <c>v{major}.{minor}.{build}</c>，與 <c>SqlAssistBuildVersion.DisplayVersion</c> 逐字相同。
/// 兩邊剖析不成 <see cref="Version"/> 就回 <see cref="SqlAssistUpdateStatus.Unknown"/>——
/// 猜一個比較結果只會在使用者手上變成「一直說有新版」或「永遠沒有新版」。
/// </remarks>
public static class SqlAssistUpdateCheck
{
    /// <summary>最新發行版本的 API；它天生排除草稿與 prerelease，正好對上先發草稿再 Publish 的流程。</summary>
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/a73013110/SqlAssist.Ssms22/releases/latest";

    /// <summary>使用者要去的地方；擴充自己不下載也不安裝 VSIX。</summary>
    public const string LatestReleasePageUrl =
        "https://github.com/a73013110/SqlAssist.Ssms22/releases/latest";

    /// <summary>兩次自動檢查之間至少隔這麼久；手動檢查不受限制。</summary>
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromDays(1);

    /// <summary>GitHub 對沒有 User-Agent 的請求直接回 403，所以這個標頭是必要的，不是禮貌。</summary>
    public const string UserAgentProduct = "SqlAssist.Ssms22";

    /// <summary>從 <c>releases/latest</c> 的回應取出結論。</summary>
    /// <param name="currentVersion"><see cref="Core.Diagnostics.SqlAssistBuildVersion.DisplayVersion"/>。</param>
    /// <param name="json">GitHub 的回應內容；讀不成 JSON 一律是「查不到」。</param>
    public static SqlAssistUpdateResult FromReleaseJson(string currentVersion, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Unknown();

        try
        {
            return Compare(currentVersion, JsonReader.Parse(json!)["tag_name"].AsString());
        }
        catch (JsonParseException)
        {
            return Unknown();
        }
    }

    /// <summary>比對目前版本與發行 tag。</summary>
    /// <param name="tag">發行 tag，例如 <c>v0.16.3</c>；前面的 <c>v</c> 可有可無。</param>
    public static SqlAssistUpdateResult Compare(string currentVersion, string? tag)
    {
        var latestText = ParseTag(tag);

        if (latestText.Length == 0 ||
            !TryParseVersion(latestText, out var latest) ||
            !TryParseVersion(currentVersion, out var current))
        {
            return Unknown();
        }

        return latest > current
            ? new SqlAssistUpdateResult(SqlAssistUpdateStatus.UpdateAvailable, latestText,
                "有新版 " + latestText + "；目前是 " + currentVersion + "。安裝前要先關掉所有 SSMS。")
            : new SqlAssistUpdateResult(SqlAssistUpdateStatus.UpToDate, latestText,
                "已是最新版 " + currentVersion + "。");
    }

    /// <summary>問不到答案：連不上、被限流、回應看不懂，對使用者都是同一件事。</summary>
    public static SqlAssistUpdateResult Unknown() =>
        new(SqlAssistUpdateStatus.Unknown, string.Empty, "查不到最新版本；請稍後再試，或直接到 GitHub 的發行頁看。");

    /// <summary>去掉 tag 前面的 <c>v</c>；不是版本形狀就回空字串。</summary>
    public static string ParseTag(string? tag)
    {
        var text = (tag ?? string.Empty).Trim();

        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);

        return TryParseVersion(text, out _) ? text : string.Empty;
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = EmptyVersion;

        // Version.TryParse 認得 "0.16" 與 "0.16.3.1234"，但不認 "0.16.3-beta"；
        // 預發行版本不會出現在 releases/latest，所以不另外剝後綴。
        if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text!.Trim(), out var parsed)) return false;

        version = parsed;
        return true;
    }

    private static readonly Version EmptyVersion = new(0, 0);
}
