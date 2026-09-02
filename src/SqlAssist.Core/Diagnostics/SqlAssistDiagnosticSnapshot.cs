using System;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Diagnostics;

/// <summary>可安全放進支援資訊的使用者動作種類；刻意沒有容納 SQL 文字的欄位。</summary>
public enum SqlAssistActivityKind
{
    None,
    SuggestionCommitted,
    SnippetExpanded,
    WildcardExpanded,
    AlterExpanded,
    InsertExpanded,
    MergeExpanded,
    ExecuteExpanded,
    FunctionCallExpanded,
    DefinitionOpened,
    ResultGridScripted
}

/// <summary>最近一次可辨識的 SqlAssist 動作。</summary>
public readonly struct SqlAssistActivity
{
    public SqlAssistActivity(
        SqlAssistActivityKind kind,
        DateTimeOffset occurredAt,
        int affectedItemCount = 0)
    {
        Kind = kind;
        OccurredAt = occurredAt;
        AffectedItemCount = Math.Max(0, affectedItemCount);
    }

    public SqlAssistActivityKind Kind { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>欄位或參數數量；不適用的動作為零。</summary>
    public int AffectedItemCount { get; }

    public bool HasValue => Kind != SqlAssistActivityKind.None;
}

/// <summary>「關於與診斷」視窗在同一個時間點取得的不可變資料。</summary>
public sealed class SqlAssistDiagnosticSnapshot
{
    public string ProductName { get; init; } = "SqlAssist for SSMS 22";

    public string Description { get; init; } =
        "SSMS 22 的 T-SQL 即時建議、程式碼片段與資料庫物件結構預覽工具。";

    public string Author { get; init; } = "Yikai";

    public string ContactEmail { get; init; } = "a73013110@gmail.com";

    public string License { get; init; } = "MIT";

    public string RepositoryUrl { get; init; } = "https://github.com/a73013110/SqlAssist.Ssms22";

    public string IssuesUrl { get; init; } = "https://github.com/a73013110/SqlAssist.Ssms22/issues";

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;

    public SqlAssistBuildVersion BuildVersion { get; init; } = SqlAssistBuildVersion.Unknown;

    public SqlAssistSettings Settings { get; init; } = new();

    public bool PackageReady { get; init; }

    public bool SettingsConnected { get; init; }

    public int OpenSqlEditorCount { get; init; }

    public bool HasActiveSqlEditor { get; init; }

    public SqlAssistActivity LastActivity { get; init; }

    public bool? NativeIntelliSenseEnabled { get; init; }

    public bool? NativeMemberListSuppressed { get; init; }

    public string SsmsVersion { get; init; } = "讀不到";

    public string OperatingSystem { get; init; } = "讀不到";

    public string RuntimeVersion { get; init; } = "讀不到";

    public string ProcessArchitecture { get; init; } = "讀不到";

    public bool LogExists { get; init; }

    public long LogSizeBytes { get; init; }

    public DateTimeOffset? LogLastUpdatedAt { get; init; }

    /// <summary>只在本機視窗顯示的完整路徑。</summary>
    public string LogPath { get; init; } = string.Empty;

    /// <summary>可貼到公開 Issue 的匿名路徑。</summary>
    public string LogPathForReport { get; init; } =
        @"%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log";

    public string PreviewWindowState { get; init; } = "讀不到";
}
