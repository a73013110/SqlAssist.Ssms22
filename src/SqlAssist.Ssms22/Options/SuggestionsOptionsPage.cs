using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SqlAssist.Core;

namespace SqlAssist.Ssms22.Options;

/// <summary>「工具 → 選項 → SqlAssist → 建議清單」頁。</summary>
[ComVisible(true)]
[Guid(PageGuidString)]
public sealed class SuggestionsOptionsPage : SqlAssistOptionsPage
{
    public const string PageGuidString = "ce7eea48-9730-4580-8d87-efe4e87e9198";

    [Category("清單")]
    [DisplayName("清單引擎")]
    [Description(
        "Native：使用平台原生 IntelliSense，支援滑鼠點選、篩選列與正確的螢幕邊界處理。" +
        "Custom：使用自製 WPF 清單，只能用鍵盤操作，且會與 SSMS 內建清單同時出現。" +
        "變更後需要重新開啟查詢視窗。")]
    public CompletionEngine Engine { get; set; } = CompletionEngine.Native;

    [Category("清單")]
    [DisplayName("關閉 SSMS 內建 IntelliSense 清單")]
    [Description(
        "顯示本擴充的清單時一併關閉 SSMS 內建的 T-SQL 清單，避免同時出現兩份。" +
        "若已在「文字編輯器 → Transact-SQL → IntelliSense」直接關閉 SSMS 內建功能，" +
        "這裡可以關掉。")]
    public bool SuppressNativeIntelliSense { get; set; } = true;

    [Category("顯示")]
    [DisplayName("顯示即時建議")]
    [Description("輸入時自動彈出建議清單。")]
    public bool Enabled { get; set; } = true;

    [Category("顯示")]
    [DisplayName("觸發字元數")]
    [Description("輸入幾個字元後才彈出清單。有效範圍 1 到 10。")]
    public int TriggerAfterCharacters { get; set; } = 1;

    [Category("顯示")]
    [DisplayName("延遲毫秒數")]
    [Description("最後一次按鍵之後等待多久才重新篩選。有效範圍 0 到 1000。")]
    public int DelayMilliseconds { get; set; } = 70;

    [Category("顯示")]
    [DisplayName("最多顯示筆數")]
    [Description("清單最多顯示幾筆。有效範圍 1 到 500。")]
    public int MaximumItems { get; set; } = 100;

    [Category("顯示")]
    [DisplayName("顯示預覽窗格")]
    [Description("在清單右側顯示選取物件的欄位結構或 SQL 定義。")]
    public bool ShowPreview { get; set; } = true;

    [Category("插入")]
    [DisplayName("補上結構描述名稱")]
    [Description("插入物件時自動加上 schema，例如 dbo.Lib_Reader。")]
    public bool QualifyObjectNames { get; set; }

    [Category("插入")]
    [DisplayName("使用方括號")]
    [Description("插入物件時加上方括號，例如 [dbo].[Lib_Reader]。")]
    public bool UseSquareBrackets { get; set; }

    private protected override void LoadFrom(SqlAssistSettings settings)
    {
        Engine = settings.Suggestions.Engine;
        SuppressNativeIntelliSense = settings.Suggestions.SuppressNativeIntelliSense;
        Enabled = settings.Suggestions.Enabled;
        TriggerAfterCharacters = settings.Suggestions.TriggerAfterCharacters;
        DelayMilliseconds = settings.Suggestions.DelayMilliseconds;
        MaximumItems = settings.Suggestions.MaximumItems;
        ShowPreview = settings.Suggestions.ShowPreview;
        QualifyObjectNames = settings.Suggestions.QualifyObjectNames;
        UseSquareBrackets = settings.Suggestions.UseSquareBrackets;
    }

    private protected override void ApplyTo(SqlAssistSettings settings)
    {
        settings.Suggestions.Engine = Engine;
        settings.Suggestions.SuppressNativeIntelliSense = SuppressNativeIntelliSense;
        settings.Suggestions.Enabled = Enabled;

        // 屬性方格允許輸入任意整數，界限在這裡收斂，讓設定檔永遠是可用值。
        settings.Suggestions.TriggerAfterCharacters = Clamp(TriggerAfterCharacters, 1, 10);
        settings.Suggestions.DelayMilliseconds = Clamp(DelayMilliseconds, 0, 1000);
        settings.Suggestions.MaximumItems = Clamp(MaximumItems, 1, 500);
        settings.Suggestions.ShowPreview = ShowPreview;
        settings.Suggestions.QualifyObjectNames = QualifyObjectNames;
        settings.Suggestions.UseSquareBrackets = UseSquareBrackets;

        LoadFrom(settings); // 把收斂後的值反映回頁面，使用者才看得到實際生效的設定。
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}
