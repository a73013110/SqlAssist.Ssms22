using System.ComponentModel;
using System.Runtime.InteropServices;
using SqlAssist.Core;

namespace SqlAssist.Ssms22.Options;

/// <summary>「工具 → 選項 → SqlAssist → 一般」頁。</summary>
[ComVisible(true)]
[Guid(PageGuidString)]
public sealed class GeneralOptionsPage : SqlAssistOptionsPage
{
    public const string PageGuidString = "444810d7-36be-4ce2-adad-0714976660dd";

    [Category("一般")]
    [DisplayName("啟用 SqlAssist")]
    [Description("關閉後不顯示任何建議，也不做 Tab 展開。相當於快捷鍵 Ctrl+Alt+Shift+S。")]
    public bool Enabled { get; set; } = true;

    [Category("功能")]
    [DisplayName("Tab 快捷展開")]
    [Description("在建議清單中顯示 ssf、ap、af 等 Snippet，並允許以 Tab 展開。")]
    public bool TabExpansion { get; set; } = true;

    [Category("功能")]
    [DisplayName("關鍵字建議")]
    [Description("在建議清單中顯示 T-SQL 關鍵字，並把小寫關鍵字展開為大寫。")]
    public bool KeywordSuggestions { get; set; } = true;

    [Category("功能")]
    [DisplayName("資料庫物件建議")]
    [Description("查詢目前連線資料庫的 Table、View、Procedure、Function 與 Schema。")]
    public bool ObjectPicker { get; set; } = true;

    [Category("功能")]
    [DisplayName("物件結構提示")]
    [Description("滑鼠停留在資料表、檢視、預存程序上時，顯示欄位型別、NULL、PK 等結構資訊。")]
    public bool ObjectHover { get; set; } = true;

    [Category("功能")]
    [DisplayName("結果格命令")]
    [Description("結果格的 Script as INSERT、Copy as IN clause。功能開發中。")]
    public bool ResultGridCommands { get; set; } = true;

    [Category("診斷")]
    [DisplayName("詳細診斷記錄")]
    [Description("把逐次建議與提交的細節寫入 %LOCALAPPDATA%\\SqlAssist.Ssms22\\SqlAssist.log。")]
    public bool DiagnosticsEnabled { get; set; }

    [Category("診斷")]
    [DisplayName("非同步 IntelliSense 探測")]
    [Description(
        "讓探測用的平台原生建議來源實際提供項目，用來確認 SSMS 是否支援新版 IntelliSense。" +
        "開啟後可能與 SSMS 原生 T-SQL 清單同時出現，僅供測試。")]
    public bool AsyncCompletionProbe { get; set; }

    private protected override void LoadFrom(SqlAssistSettings settings)
    {
        Enabled = settings.Enabled;
        TabExpansion = settings.Features.TabExpansion;
        KeywordSuggestions = settings.Features.KeywordUppercase;
        ObjectPicker = settings.Features.ObjectPicker;
        ObjectHover = settings.Features.ObjectHover;
        ResultGridCommands = settings.Features.ResultGridCommands;
        DiagnosticsEnabled = settings.DiagnosticsEnabled;
        AsyncCompletionProbe = settings.AsyncCompletionProbe;
    }

    private protected override void ApplyTo(SqlAssistSettings settings)
    {
        settings.Enabled = Enabled;
        settings.Features.TabExpansion = TabExpansion;
        settings.Features.KeywordUppercase = KeywordSuggestions;
        settings.Features.ObjectPicker = ObjectPicker;
        settings.Features.ObjectHover = ObjectHover;
        settings.Features.ResultGridCommands = ResultGridCommands;
        settings.DiagnosticsEnabled = DiagnosticsEnabled;
        settings.AsyncCompletionProbe = AsyncCompletionProbe;
    }
}
