using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Commands;

/// <summary>只在使用者開啟視窗時，從 SSMS 平台邊界取得一次診斷快照。</summary>
internal static class SqlAssistDiagnosticSnapshotFactory
{
    public static SqlAssistDiagnosticSnapshot Create()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var log = ReadLogState();

        return new SqlAssistDiagnosticSnapshot
        {
            BuildVersion = SqlAssistPackage.BuildVersion,
            Settings = SqlAssistSettingsStore.Current,
            PackageReady = SqlAssistRuntimeState.PackageReady,
            SettingsConnected = SqlAssistSettingsStore.IsConnected,
            OpenSqlEditorCount = Math.Max(0, SqlAssistRuntimeState.OpenTextViewCount),
            HasActiveSqlEditor = ActiveSqlEditor.Current is not null,
            LastActivity = SqlAssistRuntimeState.LastActivity,
            NativeIntelliSenseEnabled = SqlAssistSettingsStore.TryGetNativeIntelliSenseEnabled(),
            NativeMemberListSuppressed = NativeMemberList.TryGetSuppressed(),
            SsmsVersion = ReadSsmsVersion(),
            OperatingSystem = Environment.OSVersion.VersionString,
            RuntimeVersion = $".NET Framework CLR {Environment.Version}",
            ProcessArchitecture = Environment.Is64BitProcess ? "x64" : "x86",
            LogExists = log.Exists,
            LogSizeBytes = log.SizeBytes,
            LogLastUpdatedAt = log.LastUpdatedAt,
            LogPath = SqlAssistDiagnostics.LogPath,
            PreviewWindowState = FormatPreviewWindowState()
        };
    }

    private static string ReadSsmsVersion()
    {
        return SqlAssistPlatformGuard.Probe(
            "讀取 SSMS 版本",
            () =>
            {
                using var process = Process.GetCurrentProcess();
                var path = process.MainModule?.FileName;

                if (string.IsNullOrWhiteSpace(path))
                {
                    return "讀不到";
                }

                var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                return string.IsNullOrWhiteSpace(version) ? "讀不到" : version;
            },
            fallback: "讀不到");
    }

    private static LogState ReadLogState()
    {
        return SqlAssistPlatformGuard.Probe(
            "讀取診斷紀錄狀態",
            () =>
            {
                var file = new FileInfo(SqlAssistDiagnostics.LogPath);

                if (!file.Exists)
                {
                    return default;
                }

                return new LogState(
                    true,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTime));
            },
            fallback: default(LogState));
    }

    private static string FormatPreviewWindowState()
    {
        var stackedWidth = PreviewWindowState.StackedWidth?.ToString("0", CultureInfo.InvariantCulture)
                           ?? "自動";

        return $"上下 {stackedWidth}×{PreviewWindowState.StackedHeight.ToString("0", CultureInfo.InvariantCulture)}；" +
               $"側邊 {PreviewWindowState.BesideWidth.ToString("0", CultureInfo.InvariantCulture)}×" +
               PreviewWindowState.BesideHeight.ToString("0", CultureInfo.InvariantCulture);
    }

    private readonly struct LogState
    {
        public LogState(bool exists, long sizeBytes, DateTimeOffset? lastUpdatedAt)
        {
            Exists = exists;
            SizeBytes = sizeBytes;
            LastUpdatedAt = lastUpdatedAt;
        }

        public bool Exists { get; }

        public long SizeBytes { get; }

        public DateTimeOffset? LastUpdatedAt { get; }
    }
}
