using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlAssist.Core.Diagnostics;

/// <summary>診斷資訊裡的一列「標籤 — 值」。</summary>
public readonly struct SqlAssistDiagnosticRow
{
    public SqlAssistDiagnosticRow(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public string Value { get; }
}

/// <summary>同一個標題底下的一組診斷列。</summary>
public sealed class SqlAssistDiagnosticSection
{
    public SqlAssistDiagnosticSection(string title, IReadOnlyList<SqlAssistDiagnosticRow> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }

    public IReadOnlyList<SqlAssistDiagnosticRow> Rows { get; }
}

/// <summary>
/// 「關於與診斷」視窗與可貼出的摘要共用的欄位清單，只在這裡列一次。
/// </summary>
/// <remarks>
/// 兩邊各列一次的症狀是：新增一個設定時只改了其中一份，另一份靜靜地少一列——
/// 而少的那一份通常是回報問題時才會被看到的那一份，等於在最需要它的時候失真。
///
/// 這裡只產生字串，不決定要怎麼畫；視窗畫成兩欄，摘要印成 Markdown 清單。
/// 唯一刻意不共用的是診斷紀錄的路徑：視窗顯示完整路徑，摘要只顯示匿名路徑。
/// </remarks>
public static class SqlAssistDiagnosticSections
{
    public static IReadOnlyList<SqlAssistDiagnosticSection> DescribeSettings(
        SqlAssistDiagnosticSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var settings = snapshot.Settings;

        return new[]
        {
            new SqlAssistDiagnosticSection("一般與編輯", new[]
            {
                Row("SqlAssist", SqlAssistDiagnosticReport.FormatState(settings.Enabled)),
                Row("輸入時關鍵字轉大寫", SqlAssistDiagnosticReport.FormatState(settings.UppercaseKeywordsOnType)),

                // 這一項改變的是每一次按鍵的結果，回報「多了一個括號」時第一個要問的就是它。
                Row("分隔字元自動配對", SqlAssistDiagnosticReport.FormatState(settings.AutoPairDelimiters)),
                Row(
                    "Tab 展開 SELECT *",
                    Join(
                        SqlAssistDiagnosticReport.FormatState(settings.ExpandWildcardOnTab),
                        SqlAssistDiagnosticReport.FormatWildcardLayout(settings.WildcardLayout)))
            }),
            new SqlAssistDiagnosticSection("建議清單", new[]
            {
                Row(
                    "即時建議",
                    Join(
                        SqlAssistDiagnosticReport.FormatState(settings.SuggestionsEnabled),
                        $"{settings.TriggerAfterCharacters} 個字元後觸發")),
                Row(
                    "內容來源",
                    Join(
                        $"程式碼片段 {SqlAssistDiagnosticReport.FormatState(settings.IncludeSnippets)}",
                        $"資料庫物件 {SqlAssistDiagnosticReport.FormatState(settings.IncludeDatabaseObjects)}")),
                Row("分類篩選列", SqlAssistDiagnosticReport.FormatState(settings.ShowCategoryFilters)),
                Row(
                    "物件名稱格式",
                    Join(
                        $"結構描述 {SqlAssistDiagnosticReport.FormatState(settings.QualifyObjectNames)}",
                        $"方括號 {SqlAssistDiagnosticReport.FormatState(settings.UseSquareBrackets)}")),
                Row(
                    "語句展開",
                    Join(
                        $"INSERT {SqlAssistDiagnosticReport.FormatState(settings.ExpandInsertStatement)}",
                        $"EXEC {SqlAssistDiagnosticReport.FormatState(settings.ExpandProcedureCall)}")),
                Row(
                    "只使用 SqlAssist 清單",
                    SqlAssistDiagnosticReport.FormatState(settings.SuppressNativeMemberList))
            }),
            new SqlAssistDiagnosticSection("物件結構", new[]
            {
                Row("滑鼠停留提示", SqlAssistDiagnosticReport.FormatState(settings.HoverEnabled)),
                Row("浮動預覽", SqlAssistDiagnosticReport.FormatPreview(settings)),
                Row("預覽位置", SqlAssistDiagnosticReport.FormatPreviewPlacement(settings.PreviewPlacement)),
                Row("預覽字級", settings.PreviewFontSize.ToString("0.#", CultureInfo.InvariantCulture))
            }),
            new SqlAssistDiagnosticSection("診斷", new[]
            {
                Row("詳細診斷紀錄", SqlAssistDiagnosticReport.FormatState(settings.VerboseLogging)),
                Row("設定來源", snapshot.SettingsConnected ? "SSMS Unified Settings" : "內建預設值")
            })
        };
    }

    public static SqlAssistDiagnosticSection DescribeVersion(SqlAssistDiagnosticSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return new SqlAssistDiagnosticSection("版本", new[]
        {
            Row("版本", snapshot.BuildVersion.DisplayVersion),
            Row("完整 Build", snapshot.BuildVersion.FullVersion),
            Row("Commit", snapshot.BuildVersion.ShortCommitId)
        });
    }

    public static SqlAssistDiagnosticSection DescribeEnvironment(SqlAssistDiagnosticSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return new SqlAssistDiagnosticSection("環境", new[]
        {
            Row("SSMS", snapshot.SsmsVersion),
            Row("作業系統", snapshot.OperatingSystem),
            Row("執行環境", Join(snapshot.RuntimeVersion, snapshot.ProcessArchitecture))
        });
    }

    public static SqlAssistDiagnosticSection DescribeRuntime(SqlAssistDiagnosticSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return new SqlAssistDiagnosticSection("執行狀態", new[]
        {
            Row(
                "作用中的 SQL 編輯器",
                snapshot.OpenSqlEditorCount.ToString(CultureInfo.InvariantCulture)),
            Row("目前焦點", snapshot.HasActiveSqlEditor ? "SQL 查詢編輯器" : "不在 SQL 查詢編輯器"),
            Row("最近活動", SqlAssistDiagnosticReport.FormatActivity(snapshot.LastActivity)),
            Row("預覽視窗", snapshot.PreviewWindowState)
        });
    }

    /// <summary>同一列裡並排的兩個值；分隔符號只有這一份，兩種輸出才不會各長各的。</summary>
    private static string Join(string first, string second) => $"{first} · {second}";

    private static SqlAssistDiagnosticRow Row(string label, string value) => new(label, value);
}
