using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// <param name="IncludeRows">連資料列數一起匯出；要多讀一段 <c>sys.partitions</c>，預設不讀。</param>
internal sealed record SqlSchemaExportOptions(SqlSchemaExportScope Scope, SqlSchemaExportFormat Format, bool OpenAfter, bool IncludeRows);

/// <summary>
/// 匯出結構描述之前問三件事：哪幾張、什麼格式、要不要附資料列數與匯出後打開。
/// </summary>
/// <remarks>
/// 範圍每一列右邊一顆張數徽章，而不是只寫「所選」：主清單永遠選著一列，沒想過要多選的人按下匯出只會
/// 得到一張，這裡看到「1 張」才知道要回去 Ctrl+A。預設照選了幾張決定——多選過就是要那幾張，否則是清單上的全部。
/// 主要按鈕跟著範圍寫出張數（「匯出 76 張」），按下去之前最後一眼就是結果。
///
/// 格式是兩張並排的選項卡（<see cref="SqlAssistChrome.CreateChoiceTile"/>）：選的是一種檔案，看圖示就認得。
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
    private readonly CheckBox _includeRows = new() { IsChecked = false };
    private readonly CheckBox _openAfter = new() { IsChecked = true };
    private readonly Button _export = SqlAssistChrome.CreateButton("", Metrics, primary: true);
    private readonly (int Selected, int Matching, int All) _counts;
    private SqlSchemaExportOptions? _result;

    /// <param name="server">伺服器名稱；抬頭那一行淡色的脈絡。</param>
    /// <param name="database">資料庫名稱；抬頭的主角。</param>
    /// <param name="counts">三種範圍各幾張；是 0 的那一項停用。</param>
    /// <param name="pendingChanges">還沒套用的說明變更數；0 時不顯示資訊列。</param>
    /// <param name="preferSelected">從「匯出所選資料表」進來：只選一張也預設所選。</param>
    public SqlSchemaExportWindow(string server, string database, (int Selected, int Matching, int All) counts,
        int pendingChanges, bool preferSelected)
    {
        SqlAssistDialogs.Configure(this, DictionaryText.ExportTitle, 540, null, minWidth: 460);
        _counts = counts;

        _selected.IsEnabled = counts.Selected > 0;
        _matching.IsEnabled = counts.Matching > 0;
        var selectedFirst = counts.Selected > (preferSelected ? 0 : 1);
        (selectedFirst ? _selected : counts.Matching > 0 ? _matching : _all).IsChecked = true;

        var root = new StackPanel { Margin = SqlAssistChrome.DialogPadding };
        root.Children.Add(CreateContext(server, database));

        if (pendingChanges > 0)
        {
            var info = SqlAssistChrome.CreateInfoBar(SqlIcon.Information, DictionaryText.ExportPendingInfo(pendingChanges));
            info.Margin = new Thickness(0, 0, 0, 16);
            root.Children.Add(info);
        }

        root.Children.Add(SqlAssistChrome.CreateSection(DictionaryText.ExportScopeSection, SqlAssistChrome.CreateOptionGroup(
            ScopeRow(_selected, DictionaryText.ExportScopeSelected, DictionaryText.ExportScopeSelectedDescription, counts.Selected),
            ScopeRow(_matching, DictionaryText.ExportScopeMatching, DictionaryText.ExportScopeMatchingDescription, counts.Matching),
            ScopeRow(_all, DictionaryText.ExportScopeAll, DictionaryText.ExportScopeAllDescription, counts.All)), first: true));

        root.Children.Add(SqlAssistChrome.CreateSection(DictionaryText.ExportFormatSection, CreateFormats()));

        root.Children.Add(SqlAssistChrome.CreateSection(DictionaryText.ExportOptionsSection, SqlAssistChrome.CreateOptionGroup(
            SqlAssistChrome.CreateOptionRow(_includeRows, DictionaryText.ExportIncludeRows, DictionaryText.ExportIncludeRowsDescription),
            SqlAssistChrome.CreateOptionRow(_openAfter, DictionaryText.ExportOpenAfter, DictionaryText.ExportOpenAfterDescription))));

        var cancel = SqlAssistChrome.CreateButton(CommonText.Cancel, Metrics);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();

        _export.IsDefault = true;
        _export.Click += (_, _) =>
        {
            _result = new SqlSchemaExportOptions(Scope(),
                _markdown.IsChecked == true ? SqlSchemaExportFormat.Markdown : SqlSchemaExportFormat.Excel,
                _openAfter.IsChecked == true, _includeRows.IsChecked == true);
            Close();
        };
        foreach (var scope in new ToggleButton[] { _selected, _matching, _all }) scope.Checked += (_, _) => UpdateExport();
        UpdateExport();

        root.Children.Add(SqlAssistChrome.CreateDialogFooter(null, cancel, _export));
        Content = root;
    }

    /// <summary>以強制回應開啟；取消時回 null。</summary>
    public SqlSchemaExportOptions? Ask()
    {
        ShowModal();
        return _result;
    }

    private SqlSchemaExportScope Scope() =>
        _selected.IsChecked == true ? SqlSchemaExportScope.Selected
        : _matching.IsChecked == true ? SqlSchemaExportScope.Matching
        : SqlSchemaExportScope.All;

    /// <summary>主要按鈕寫出這一次會匯出幾張；換範圍時跟著換。</summary>
    private void UpdateExport()
    {
        var count = Scope() switch
        {
            SqlSchemaExportScope.Selected => _counts.Selected,
            SqlSchemaExportScope.Matching => _counts.Matching,
            _ => _counts.All,
        };
        _export.Content = DictionaryText.ExportTablesButton(count);
        _export.IsEnabled = count > 0;
    }

    /// <summary>抬頭：資料庫圖示、資料庫名稱（主角）與伺服器（淡色）。</summary>
    private static FrameworkElement CreateContext(string server, string database)
    {
        var icon = SqlAssistChrome.CreateIcon(SqlIcon.Database);
        icon.Width = 24; icon.Height = 24;
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
        {
            Text = database, FontFamily = SqlAssistChrome.InterfaceFont, FontSize = Metrics.Title + 1, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.WindowForeground));
        names.Children.Add(SqlAssistChrome.CreateMetadataText(server, Metrics));

        var context = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        context.Children.Add(icon);
        context.Children.Add(names);
        return context;
    }

    /// <summary>範圍的一列：右邊一顆張數徽章，0 張時整列停用。</summary>
    private static Border ScopeRow(RadioButton box, string title, string description, int count)
    {
        var badge = SqlAssistChrome.CreateBadge(DictionaryText.ExportTableCount(count), Metrics);
        badge.IsEnabled = count > 0;
        badge.Opacity = count > 0 ? 1 : 0.5;
        return SqlAssistChrome.CreateOptionRow(box, title, description, badge);
    }

    /// <summary>兩種格式並排成兩張選項卡，中間留 8。</summary>
    private Grid CreateFormats()
    {
        var formats = new Grid();
        formats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        formats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var excel = SqlAssistChrome.CreateChoiceTile(_excel, SqlIcon.Spreadsheet, DictionaryText.ExportExcel, DictionaryText.ExportExcelDescription);
        var markdown = SqlAssistChrome.CreateChoiceTile(_markdown, SqlIcon.Markdown, DictionaryText.ExportMarkdown, DictionaryText.ExportMarkdownDescription);
        Grid.SetColumn(markdown, 2);
        formats.Children.Add(excel);
        formats.Children.Add(markdown);
        return formats;
    }
}
