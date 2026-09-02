using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Diagnostics;

public enum SqlAssistHealthLevel
{
    Ready,
    Information,
    Warning
}

public sealed class SqlAssistHealthCheck
{
    public SqlAssistHealthCheck(
        string name,
        string status,
        string detail,
        SqlAssistHealthLevel level)
    {
        Name = name;
        Status = status;
        Detail = detail;
        Level = level;
    }

    public string Name { get; }

    public string Status { get; }

    public string Detail { get; }

    public SqlAssistHealthLevel Level { get; }
}

/// <summary>把整組健康檢查收斂成一句話；視窗的徽章與摘要的抬頭都用這一份。</summary>
/// <remarks>
/// 各自判斷一次的症狀是：徽章說「狀態良好」而抬頭說「已暫停」。
/// 圖示與強調色由呼叫端依 <see cref="Level"/> 決定，那才是外觀。
/// </remarks>
public sealed class SqlAssistHealthSummary
{
    public SqlAssistHealthSummary(
        SqlAssistHealthLevel level,
        int warningCount,
        string headline,
        string shortStatus,
        string detail)
    {
        Level = level;
        WarningCount = warningCount;
        Headline = headline;
        ShortStatus = shortStatus;
        Detail = detail;
    }

    public SqlAssistHealthLevel Level { get; }

    public int WarningCount { get; }

    /// <summary>一行結論，例如「SqlAssist 運作正常」。</summary>
    public string Headline { get; }

    /// <summary>徽章用的極短狀態。</summary>
    public string ShortStatus { get; }

    public string Detail { get; }
}

/// <summary>把平台快照整理成人能判讀、也能安全貼到公開 Issue 的支援資訊。</summary>
public static class SqlAssistDiagnosticReport
{
    public static IReadOnlyList<SqlAssistHealthCheck> EvaluateHealth(
        SqlAssistDiagnosticSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var checks = new List<SqlAssistHealthCheck>
        {
            snapshot.PackageReady
                ? Ready("SqlAssist 套件", "已載入", "工具選單與命令已就緒。")
                : Warning("SqlAssist 套件", "尚未就緒", "請重新啟動 SSMS；若仍發生，請附上診斷紀錄。"),
            snapshot.SettingsConnected
                ? Ready("設定服務", "已連線", "正在使用 SSMS Unified Settings。")
                : Warning("設定服務", "使用預設值", "讀不到 Unified Settings，目前以內建預設值運作。"),
            snapshot.Settings.Enabled
                ? Ready("功能狀態", "已啟用", "SqlAssist 的總開關目前已開啟。")
                : Information("功能狀態", "已暫停", "總開關目前已關閉；這是設定狀態，不是載入失敗。")
        };

        checks.Add(snapshot.OpenSqlEditorCount > 0
            ? Ready(
                "SQL 編輯器整合",
                $"{snapshot.OpenSqlEditorCount} 個作用中",
                "SqlAssist 已接上目前開著的 SQL 查詢編輯器。")
            : Information(
                "SQL 編輯器整合",
                "等待查詢視窗",
                "目前沒有開著的 SQL 查詢編輯器；開啟一個查詢視窗即可驗證。"));

        checks.Add(snapshot.NativeIntelliSenseEnabled switch
        {
            true => Ready(
                "SSMS T-SQL IntelliSense",
                "已啟用",
                "紅色錯誤波浪線與大綱功能可用。"),
            false => Warning(
                "SSMS T-SQL IntelliSense",
                "已停用",
                "建議重新啟用；SqlAssist 只會擋掉互搶的自動建議清單。"),
            null => Information(
                "SSMS T-SQL IntelliSense",
                "無法確認",
                "SSMS 未回傳目前狀態。")
        });

        checks.Add(MemberListHealth(snapshot));
        return checks;
    }

    public static SqlAssistHealthSummary Summarize(SqlAssistDiagnosticSnapshot snapshot)
    {
        return Summarize(snapshot, EvaluateHealth(snapshot));
    }

    /// <summary>已經算過健康檢查時走這一個，同一份清單不必再評估一次。</summary>
    public static SqlAssistHealthSummary Summarize(
        SqlAssistDiagnosticSnapshot snapshot,
        IReadOnlyList<SqlAssistHealthCheck> checks)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (checks is null)
        {
            throw new ArgumentNullException(nameof(checks));
        }

        var warningCount = 0;

        for (var index = 0; index < checks.Count; index++)
        {
            if (checks[index].Level == SqlAssistHealthLevel.Warning)
            {
                warningCount++;
            }
        }

        if (warningCount > 0)
        {
            return new SqlAssistHealthSummary(
                SqlAssistHealthLevel.Warning,
                warningCount,
                $"有 {warningCount} 項需要注意",
                $"{warningCount} 項需注意",
                "健康檢查清單裡標成警告的項目就是原因與建議的處理方式。");
        }

        if (!snapshot.Settings.Enabled)
        {
            return new SqlAssistHealthSummary(
                SqlAssistHealthLevel.Information,
                0,
                "SqlAssist 目前已暫停",
                "已暫停",
                "總開關目前已關閉；套件仍正常載入，可隨時從工具選單重新啟用。");
        }

        if (snapshot.OpenSqlEditorCount == 0)
        {
            return new SqlAssistHealthSummary(
                SqlAssistHealthLevel.Information,
                0,
                "SqlAssist 已載入，等待 SQL 查詢視窗",
                "待命",
                "開啟一個查詢視窗即可驗證建議清單與展開功能。");
        }

        return new SqlAssistHealthSummary(
            SqlAssistHealthLevel.Ready,
            0,
            "SqlAssist 運作正常",
            "狀態良好",
            "套件、設定服務與 SSMS 相容狀態均未發現異常。");
    }

    public static string Create(SqlAssistDiagnosticSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var checks = EvaluateHealth(snapshot);
        var report = new StringBuilder();
        report.AppendLine("## SqlAssist 關於與診斷");
        report.AppendLine();
        report.AppendLine("> 隱私說明：此摘要不包含 SQL 文字、伺服器名稱、資料庫名稱或 Windows 使用者名稱。");
        report.AppendLine();
        report.AppendLine("### 關於");
        AppendRows(report, SqlAssistDiagnosticSections.DescribeVersion(snapshot));
        AppendRows(report, SqlAssistDiagnosticSections.DescribeEnvironment(snapshot));
        report.AppendLine();
        report.AppendLine("### 健康檢查");
        Append(report, "整體狀態", Summarize(snapshot, checks).Headline);

        foreach (var check in checks)
        {
            report.Append("- ")
                .Append(check.Name)
                .Append("：")
                .Append(check.Status)
                .Append(" — ")
                .AppendLine(check.Detail);
        }

        report.AppendLine();
        report.AppendLine("### 重要設定");

        foreach (var section in SqlAssistDiagnosticSections.DescribeSettings(snapshot))
        {
            report.Append("#### ").AppendLine(section.Title);
            AppendRows(report, section);
            report.AppendLine();
        }

        report.AppendLine("### 執行狀態");
        AppendRows(report, SqlAssistDiagnosticSections.DescribeRuntime(snapshot));
        Append(
            report,
            "診斷紀錄檔",
            snapshot.LogExists
                ? $"存在 · {FormatBytes(snapshot.LogSizeBytes)} · {snapshot.LogPathForReport}"
                : $"尚未建立 · {snapshot.LogPathForReport}");
        Append(report, "產生時間", snapshot.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        return report.ToString().TrimEnd();
    }

    public static string FormatState(bool enabled) => enabled ? "啟用" : "停用";

    public static string FormatWildcardLayout(SqlWildcardLayout layout)
    {
        return layout switch
        {
            SqlWildcardLayout.OnePerLine => "永遠每欄一行",
            SqlWildcardLayout.OneLineWhenShort => "放得下排一行，否則每欄一行",
            SqlWildcardLayout.FillWidth => "依行寬排滿",
            _ => layout.ToString()
        };
    }

    public static string FormatPreview(SqlAssistSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return settings.PreviewMode switch
        {
            SqlPreviewMode.Off => "不顯示浮動預覽",
            SqlPreviewMode.RightArrow => "按向右鍵展開",
            SqlPreviewMode.Delay => $"停留 {settings.PreviewDelayMilliseconds} ms 後展開",
            _ => settings.PreviewMode.ToString()
        };
    }

    public static string FormatPreviewPlacement(SqlPreviewPlacement placement)
    {
        return placement switch
        {
            SqlPreviewPlacement.Stacked => "建議清單的上方或下方",
            SqlPreviewPlacement.Beside => "建議清單的側邊",
            _ => placement.ToString()
        };
    }

    public static string FormatActivity(SqlAssistActivity activity)
    {
        if (!activity.HasValue)
        {
            return "本次工作階段尚無活動";
        }

        var action = activity.Kind switch
        {
            SqlAssistActivityKind.SuggestionCommitted => "提交建議項目",
            SqlAssistActivityKind.SnippetExpanded => "展開程式碼片段",
            SqlAssistActivityKind.WildcardExpanded => WithCount("展開 SELECT *", activity, "個欄位"),
            SqlAssistActivityKind.AlterExpanded => "展開 ALTER 定義",
            SqlAssistActivityKind.InsertExpanded => WithCount("展開 INSERT", activity, "個欄位"),
            SqlAssistActivityKind.MergeExpanded => WithCount("展開 MERGE", activity, "個欄位"),
            SqlAssistActivityKind.ExecuteExpanded => WithCount("展開 EXEC", activity, "個參數"),
            SqlAssistActivityKind.FunctionCallExpanded => WithCount("補上函式引數", activity, "個引數"),
            SqlAssistActivityKind.DefinitionOpened => "在新查詢視窗開啟定義",
            _ => "未知活動"
        };

        return $"{activity.OccurredAt:yyyy-MM-dd HH:mm:ss} · {action}";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / (1024d * 1024d):0.#} MB";
    }

    private static SqlAssistHealthCheck MemberListHealth(SqlAssistDiagnosticSnapshot snapshot)
    {
        var shouldSuppress = snapshot.Settings.Enabled &&
                             snapshot.Settings.SuggestionsEnabled &&
                             snapshot.Settings.SuppressNativeMemberList;

        if (snapshot.NativeMemberListSuppressed is null)
        {
            return Information(
                "建議清單協調",
                "無法確認",
                "SSMS 未回傳內建自動建議清單的實際狀態。");
        }

        if (snapshot.NativeMemberListSuppressed == shouldSuppress)
        {
            return shouldSuppress
                ? Ready("建議清單協調", "已套用", "內建自動清單已擋下，其他 IntelliSense 功能保留。")
                : Ready("建議清單協調", "已還原", "SSMS 內建自動建議清單可正常顯示。");
        }

        return Warning(
            "建議清單協調",
            "設定尚未生效",
            shouldSuppress
                ? "設定要求只使用 SqlAssist，但 SSMS 內建自動清單仍會彈出。"
                : "設定要求還原 SSMS 內建清單，但它目前仍被擋下。");
    }

    private static string WithCount(string action, SqlAssistActivity activity, string unit)
    {
        return activity.AffectedItemCount > 0
            ? $"{action}（{activity.AffectedItemCount} {unit}）"
            : action;
    }

    private static SqlAssistHealthCheck Ready(string name, string status, string detail) =>
        new(name, status, detail, SqlAssistHealthLevel.Ready);

    private static SqlAssistHealthCheck Information(string name, string status, string detail) =>
        new(name, status, detail, SqlAssistHealthLevel.Information);

    private static SqlAssistHealthCheck Warning(string name, string status, string detail) =>
        new(name, status, detail, SqlAssistHealthLevel.Warning);

    private static void AppendRows(StringBuilder report, SqlAssistDiagnosticSection section)
    {
        foreach (var row in section.Rows)
        {
            Append(report, row.Label, row.Value);
        }
    }

    private static void Append(StringBuilder report, string label, string value)
    {
        report.Append("- ")
            .Append(label)
            .Append("：")
            .AppendLine(value);
    }
}
