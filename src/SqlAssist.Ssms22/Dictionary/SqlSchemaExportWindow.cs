using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.Localization;
using SqlAssist.Metadata.Dictionary;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Dictionary;

/// <summary>匯出哪幾張表。</summary>
internal enum SqlSchemaExportScope
{
    /// <summary>清單上選定的那幾張。</summary>
    Selected,

    /// <summary>清單上現在看得到的每一張（照過濾與條件）。</summary>
    Matching,

    /// <summary>整個資料庫的資料表與檢視，不看過濾。</summary>
    All
}

/// <summary>匯出對話框的結果。</summary>
internal sealed record SqlSchemaExportOptions(SqlSchemaExportScope Scope, SqlSchemaExportFormat Format, bool OpenAfter);

/// <summary>
/// 匯出結構描述之前問三件事：哪幾張、什麼格式、匯出後要不要打開。
/// </summary>
/// <remarks>
/// 範圍寫出張數而不是只寫「所選」：主清單永遠選著一列，沒想過要多選的人按下匯出只會得到一張，
/// 這裡看到「1 張」才知道要回去 Ctrl+A。預設照選了幾張決定——多選過就是要那幾張，否則是清單上的全部。
/// 還有沒套用的說明時多一條資訊列：匯出的是畫面上那一份，與伺服器上不一定一樣。
/// </remarks>
internal sealed class SqlSchemaExportWindow : DialogWindow
{
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    private readonly RadioButton _selected = new() { GroupName = "scope" };
    private readonly RadioButton _matching = new() { GroupName = "scope" };
    private readonly RadioButton _all = new() { GroupName = "scope" };
    private readonly RadioButton _excel = new() { GroupName = "format", IsChecked = true };
    private readonly RadioButton _markdown = new() { GroupName = "format" };
    private readonly CheckBox _openAfter = new() { IsChecked = true };
    private SqlSchemaExportOptions? _result;

    /// <param name="counts">三種範圍各幾張；所選是 0 時那一項停用。</param>
    /// <param name="pendingChanges">還沒套用的說明變更數；0 時不顯示資訊列。</param>
    /// <param name="preferSelected">從「匯出所選資料表」進來：只選一張也預設所選。</param>
    public SqlSchemaExportWindow(string summary, (int Selected, int Matching, int All) counts, int pendingChanges, bool preferSelected)
    {
        SqlAssistDialogs.Configure(this, DictionaryText.ExportTitle, 520, null, minWidth: 420);

        _selected.IsEnabled = counts.Selected > 0;
        _matching.IsEnabled = counts.Matching > 0;
        var selectedFirst = counts.Selected > (preferSelected ? 0 : 1);
        (selectedFirst ? _selected : counts.Matching > 0 ? _matching : _all).IsChecked = true;

        var root = new StackPanel { Margin = SqlAssistChrome.DialogPadding };
        var metadata = SqlAssistChrome.CreateMetadataText(summary, Metrics);
        metadata.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(metadata);

        if (pendingChanges > 0)
        {
            var info = SqlAssistChrome.CreateInfoBar(SqlIcon.Information, DictionaryText.ExportPendingInfo(pendingChanges));
            info.Margin = new Thickness(0, 0, 0, 12);
            root.Children.Add(info);
        }

        root.Children.Add(SqlAssistChrome.CreateSection(DictionaryText.ExportScopeSection, SqlAssistChrome.CreateOptionGroup(
            SqlAssistChrome.CreateOptionRow(_selected, DictionaryText.ExportScopeSelected, DictionaryText.ExportScopeSelectedDescription(counts.Selected)),
            SqlAssistChrome.CreateOptionRow(_matching, DictionaryText.ExportScopeMatching, DictionaryText.ExportScopeMatchingDescription(counts.Matching)),
            SqlAssistChrome.CreateOptionRow(_all, DictionaryText.ExportScopeAll, DictionaryText.ExportScopeAllDescription(counts.All))), first: true));

        root.Children.Add(SqlAssistChrome.CreateSection(DictionaryText.ExportFormatSection, SqlAssistChrome.CreateOptionGroup(
            SqlAssistChrome.CreateOptionRow(_excel, DictionaryText.ExportExcel, DictionaryText.ExportExcelDescription),
            SqlAssistChrome.CreateOptionRow(_markdown, DictionaryText.ExportMarkdown, DictionaryText.ExportMarkdownDescription))));

        var open = SqlAssistChrome.CreateOptionGroup(
            SqlAssistChrome.CreateOptionRow(_openAfter, DictionaryText.ExportOpenAfter, DictionaryText.ExportOpenAfterDescription));
        open.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(open);

        var cancel = SqlAssistChrome.CreateButton(CommonText.Cancel, Metrics);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();

        var export = SqlAssistChrome.CreateButton(DictionaryText.ExportButton, Metrics, primary: true);
        export.IsDefault = true;
        export.Click += (_, _) =>
        {
            _result = new SqlSchemaExportOptions(
                _selected.IsChecked == true ? SqlSchemaExportScope.Selected : _matching.IsChecked == true ? SqlSchemaExportScope.Matching : SqlSchemaExportScope.All,
                _markdown.IsChecked == true ? SqlSchemaExportFormat.Markdown : SqlSchemaExportFormat.Excel,
                _openAfter.IsChecked == true);
            Close();
        };

        root.Children.Add(SqlAssistChrome.CreateDialogFooter(null, cancel, export));
        Content = root;
    }

    /// <summary>以強制回應開啟；取消時回 null。</summary>
    public SqlSchemaExportOptions? Ask()
    {
        ShowModal();
        return _result;
    }
}
