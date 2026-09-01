using System;
using System.Linq;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Diagnostics;

public sealed class SqlAssistDiagnosticReportTests
{
    [Fact]
    public void 摘要顯示發布版號與匿名紀錄路徑()
    {
        var snapshot = CreateSnapshot();

        var report = SqlAssistDiagnosticReport.Create(snapshot);

        Assert.Contains("版本：0.14.58", report);
        Assert.Contains("Commit：bc1c421", report);
        Assert.Contains(@"%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log", report);
        Assert.DoesNotContain(@"C:\Users\PrivateUser", report);
        Assert.Contains("不包含 SQL 文字", report);
    }

    [Fact]
    public void 內建清單的設定與實際狀態不一致時標成警告()
    {
        var snapshot = CreateSnapshot(nativeMemberListSuppressed: false);

        var check = SqlAssistDiagnosticReport
            .EvaluateHealth(snapshot)
            .Single(item => item.Name == "建議清單協調");

        Assert.Equal(SqlAssistHealthLevel.Warning, check.Level);
        Assert.Equal("設定尚未生效", check.Status);
    }

    [Fact]
    public void 最近活動只顯示動作種類與數量()
    {
        var activity = new SqlAssistActivity(
            SqlAssistActivityKind.WildcardExpanded,
            new DateTimeOffset(2026, 9, 1, 20, 30, 0, TimeSpan.FromHours(8)),
            affectedItemCount: 12);

        var text = SqlAssistDiagnosticReport.FormatActivity(activity);

        Assert.Equal("2026-09-01 20:30:00 · 展開 SELECT *（12 個欄位）", text);
    }

    [Fact]
    public void 預覽與欄位排版使用可讀名稱而不是列舉字面值()
    {
        var settings = new SqlAssistSettings
        {
            PreviewMode = SqlPreviewMode.RightArrow,
            WildcardLayout = SqlWildcardLayout.FillWidth
        };

        Assert.Equal("按向右鍵展開", SqlAssistDiagnosticReport.FormatPreview(settings));
        Assert.Equal("依行寬排滿", SqlAssistDiagnosticReport.FormatWildcardLayout(settings.WildcardLayout));
    }

    /// <remarks>
    /// 視窗與摘要各列一次設定就是這個功能唯一會安靜失真的方式：新增一個設定時
    /// 只改了視窗，回報問題時看到的那一份就少一列。兩邊同源之後，這一條保證
    /// 共用的清單真的有被摘要印出來。
    /// </remarks>
    [Fact]
    public void 摘要印出共用設定區塊的每一列()
    {
        var snapshot = CreateSnapshot();

        var report = SqlAssistDiagnosticReport.Create(snapshot);

        foreach (var section in SqlAssistDiagnosticSections.DescribeSettings(snapshot))
        {
            Assert.Contains(section.Title, report);

            foreach (var row in section.Rows)
            {
                Assert.Contains($"- {row.Label}：{row.Value}", report);
            }
        }
    }

    [Fact]
    public void 有警告時整體結論蓋過總開關已暫停()
    {
        // 總開關關掉時就不該再擋內建清單，實際卻仍擋著——這一項是警告。
        var snapshot = CreateSnapshot(
            nativeMemberListSuppressed: true,
            settings: new SqlAssistSettings { Enabled = false });

        var summary = SqlAssistDiagnosticReport.Summarize(snapshot);

        Assert.Equal(SqlAssistHealthLevel.Warning, summary.Level);
        Assert.Equal(1, summary.WarningCount);
        Assert.Contains("需要注意", summary.Headline);
    }

    [Fact]
    public void 沒有警告但總開關關閉時結論是已暫停而不是異常()
    {
        var snapshot = CreateSnapshot(
            nativeMemberListSuppressed: false,
            settings: new SqlAssistSettings { Enabled = false });

        var summary = SqlAssistDiagnosticReport.Summarize(snapshot);

        Assert.Equal(SqlAssistHealthLevel.Information, summary.Level);
        Assert.Equal("已暫停", summary.ShortStatus);
    }

    [Fact]
    public void 一切正常時結論是運作正常()
    {
        var summary = SqlAssistDiagnosticReport.Summarize(CreateSnapshot());

        Assert.Equal(SqlAssistHealthLevel.Ready, summary.Level);
        Assert.Equal(0, summary.WarningCount);
        Assert.Equal("SqlAssist 運作正常", summary.Headline);
    }

    private static SqlAssistDiagnosticSnapshot CreateSnapshot(
        bool? nativeMemberListSuppressed = true,
        SqlAssistSettings? settings = null)
    {
        return new SqlAssistDiagnosticSnapshot
        {
            GeneratedAt = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.FromHours(8)),
            BuildVersion = SqlAssistBuildVersion.Create(
                "0.14.58+bc1c42183c",
                "0.14.58.48156",
                "0.14.0.0"),
            Settings = settings ?? new SqlAssistSettings(),
            PackageReady = true,
            SettingsConnected = true,
            OpenSqlEditorCount = 1,
            HasActiveSqlEditor = true,
            NativeIntelliSenseEnabled = true,
            NativeMemberListSuppressed = nativeMemberListSuppressed,
            SsmsVersion = "22.9.12120.119",
            OperatingSystem = "Windows 11",
            RuntimeVersion = ".NET CLR 4.0.30319",
            ProcessArchitecture = "x64",
            LogExists = true,
            LogSizeBytes = 2048,
            LogPath = @"C:\Users\PrivateUser\AppData\Local\SqlAssist.Ssms22\SqlAssist.log",
            LogPathForReport = @"%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log",
            PreviewWindowState = "上下 自動×420；側邊 560×420"
        };
    }
}
