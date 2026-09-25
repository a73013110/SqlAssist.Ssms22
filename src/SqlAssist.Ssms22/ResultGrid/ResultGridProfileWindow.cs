using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Tabular;
using SqlAssist.Metadata.ResultGrid;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.ResultGrid;

/// <summary>
/// 每一欄的統計摘要，一欄一列。
/// </summary>
/// <remarks>
/// 用視窗而不是產指令碼：這份東西的用途是<b>看</b>，不是貼。178 欄的摘要塞進查詢
/// 視窗變成 178 行註解，比原本捲格線還難讀；擺成一張表才排得出「整欄都是 NULL 的
/// 那幾欄」。要帶走的人按「複製」拿 TSV 與 HTML 表格，貼進哪裡都能排。
///
/// 外觀全部走 <see cref="SqlAssistChrome"/>，一個樣式都不自己定義——
/// 這是自製 UI 準則的「禁止在 UI/SqlAssistChrome 之外另立一套外觀」。
/// </remarks>
internal sealed class ResultGridProfileWindow : DialogWindow
{
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    private readonly ICollectionView _profileView;
    private readonly TextBlock _statusText;

    public ResultGridProfileWindow(ResultGridTable table, IReadOnlyList<ResultGridColumnProfile> profiles)
    {
        // 每個視窗持有自己的 View，篩選與排序不影響來源結果或其他視窗。
        _profileView = new ListCollectionView(profiles.ToList());
        SqlAssistDialogs.Configure(this, ResultGridWindowText.ProfileWindowTitle, 1040, 620, minWidth: 640, minHeight: 400);

        _statusText = SqlAssistChrome.CreateStatusText(Metrics);
        Content = BuildLayout(table);
    }

    private Grid BuildLayout(ResultGridTable table)
    {
        var root = new Grid { Margin = SqlAssistChrome.DialogPadding };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = SqlAssistChrome.CreateMetadataText(Describe(table), Metrics);
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var grid = CreateGrid();
        var body = new Grid();
        body.Children.Add(grid);
        var empty = SqlAssistChrome.CreateHint(ResultGridWindowText.NoMatchingColumns, Metrics);
        empty.Margin = new Thickness(16);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;
        empty.Visibility = Visibility.Collapsed;
        empty.IsHitTestVisible = false;
        body.Children.Add(empty);

        var surface = SqlAssistChrome.CreateSurface(body);
        Grid.SetRow(surface, 2);

        var copy = SqlAssistChrome.CreateButton(ResultGridWindowText.CopyTable, Metrics);
        copy.ToolTip = ResultGridWindowText.CopyTableTip;
        copy.Click += OnCopy;

        var toolbar = new DockPanel { Margin = new Thickness(0, 12, 0, 12) };
        var count = SqlAssistChrome.CreateMetadataText(string.Empty, Metrics);
        count.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(count, Dock.Right);
        toolbar.Children.Add(count);
        var kind = SqlAssistChrome.CreateComboBox(Metrics);
        kind.ItemsSource = new[] { ResultGridWindowText.AllColumns, ResultGridWindowText.AllNull, ResultGridWindowText.SingleValue };
        kind.SelectedIndex = 0;
        kind.Width = 136;
        kind.Margin = new Thickness(0, 0, 12, 0);
        kind.ToolTip = ResultGridWindowText.KindFilterTip;
        AutomationProperties.SetName(kind, ResultGridWindowText.KindFilterName);
        DockPanel.SetDock(kind, Dock.Left);
        toolbar.Children.Add(kind);
        var search = SqlAssistChrome.CreateTextBox(Metrics);
        AutomationProperties.SetName(search, ResultGridWindowText.SearchName);
        var label = SqlAssistChrome.CreateMetadataText(ResultGridWindowText.Filter, Metrics);
        label.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(label, Dock.Left);
        toolbar.Children.Add(label);
        search.ToolTip = ResultGridWindowText.SearchTip;
        toolbar.Children.Add(search);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);
        // 視覺樹順序也要由上到下，Tab 才會先到篩選，再進入表格。
        root.Children.Add(surface);

        void RefreshCount()
        {
            count.Text = ResultGridWindowText.ColumnCount(grid.Items.Count, table.Columns.Count);
            empty.Visibility = grid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            copy.IsEnabled = grid.Items.Count > 0;
        }

        void RefreshFilter()
        {
            var query = search.Text.Trim();
            _profileView.Filter = item => item is ResultGridColumnProfile profile &&
                (kind.SelectedIndex == 0 || (kind.SelectedIndex == 1 ? profile.IsAllNull : profile.IsConstant)) &&
                (profile.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 profile.DataType.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            _statusText.Text = string.Empty;
            RefreshCount();
        }
        search.TextChanged += (_, _) => RefreshFilter();
        kind.SelectionChanged += (_, _) => RefreshFilter();
        RefreshCount();

        var close = SqlAssistChrome.CreateButton(CommonText.Close, Metrics, primary: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        var footer = SqlAssistChrome.CreateDialogFooter(new[] { copy }, _statusText, close);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return root;
    }

    /// <remarks>
    /// 欄位順序照「查問題時的閱讀順序」排：先確認這是哪一欄、什麼型別，
    /// 再看它有沒有值（<c>NULL</c>、空字串），再看它有幾種值，最後才是範圍。
    /// 相異值放在範圍前面，因為「其實只有一個值」比「範圍是多少」更早需要知道。
    /// </remarks>
    private DataGrid CreateGrid()
    {
        var grid = SqlAssistChrome.CreateDataGrid(Metrics, transparent: true);
        grid.IsReadOnly = true;
        grid.ItemsSource = _profileView;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.FrozenColumnCount = 1;
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        AutomationProperties.SetName(grid, ResultGridWindowText.ProfileGridName);

        var cellText = SqlAssistChrome.CreateCellTextStyle();
        var numberText = SqlAssistChrome.CreateCellTextStyle(TextAlignment.Right);

        Add(grid, CommonText.Column, nameof(ResultGridColumnProfile.Name), 180, cellText);
        Add(grid, CommonText.Type, nameof(ResultGridColumnProfile.DataType), 120, cellText);
        Add(grid, ResultGridWindowText.NullCount, nameof(ResultGridColumnProfile.NullCount), 80, numberText, numeric: true);
        Add(grid, ResultGridWindowText.EmptyTextCount, nameof(ResultGridColumnProfile.EmptyTextCount), 80, numberText, numeric: true);
        Add(grid, ResultGridWindowText.DistinctCount, nameof(ResultGridColumnProfile.DistinctCount), 80, numberText, numeric: true,
            tooltip: ResultGridWindowText.DistinctCountTip);
        Add(grid, ResultGridWindowText.TextLength, nameof(ResultGridColumnProfile.TextLength), 110, cellText,
            tooltip: ResultGridWindowText.TextLengthTip);
        Add(grid, ResultGridWindowText.Minimum, nameof(ResultGridColumnProfile.Minimum), 140, cellText,
            tooltip: ResultGridWindowText.MinMaxTip);
        Add(grid, ResultGridWindowText.Maximum, nameof(ResultGridColumnProfile.Maximum), 1, cellText, star: true,
            tooltip: ResultGridWindowText.MinMaxTip);

        return grid;
    }

    private static void Add(
        DataGrid grid,
        string header,
        string property,
        double width,
        Style cellText,
        bool star = false,
        bool numeric = false,
        string? tooltip = null)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property)
            {
                StringFormat = numeric ? "N0" : null,
                ConverterCulture = CultureInfo.InvariantCulture
            },
            IsReadOnly = true,
            ElementStyle = cellText,
            HeaderStyle = SqlAssistChrome.CreateColumnHeaderStyle(Metrics,
                numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left, tooltip),
            MinWidth = star ? 140 : 60,
            Width = star
                ? new DataGridLength(width, DataGridLengthUnitType.Star)
                : new DataGridLength(width)
        });
    }

    private static string Describe(ResultGridTable table) =>
        table.IsWholeResult
            ? ResultGridWindowText.ProfileSummaryWholeResult(table.Columns.Count, table.Rows.Count)
            : ResultGridWindowText.ProfileSummarySelection(table.Columns.Count, table.Rows.Count);

    /// <summary>複製的欄與畫面上的欄同名同序；數字不帶千分位，貼進 Excel 才是數值。</summary>
    /// <remarks>每次複製重建，表頭才會跟著目前的介面語言。</remarks>
    private static SqlTabularColumn<ResultGridColumnProfile>[] CopyColumns() =>
        new SqlTabularColumn<ResultGridColumnProfile>[]
        {
            new(CommonText.Column, profile => profile.Name),
            new(CommonText.Type, profile => profile.DataType),
            new(ResultGridWindowText.NullCount, profile => profile.NullCount.ToString(CultureInfo.InvariantCulture)),
            new(ResultGridWindowText.EmptyTextCount, profile => profile.EmptyTextCount.ToString(CultureInfo.InvariantCulture)),
            new(ResultGridWindowText.DistinctCount, profile => profile.DistinctCount.ToString(CultureInfo.InvariantCulture)),
            new(ResultGridWindowText.TextLength, profile => profile.TextLength),
            new(ResultGridWindowText.Minimum, profile => profile.Minimum),
            new(ResultGridWindowText.Maximum, profile => profile.Maximum)
        };


    /// <remarks>
    /// 與清單的批次複製同一份產生器：TSV 給查詢視窗與記事本，HTML 給 Excel、Word 與郵件，
    /// 順序照目前的篩選與排序。最小值、最大值是 T-SQL 字面值，字串裡的換行與定位字元
    /// 照 Excel 規則包引號，不會把一欄拆成好幾列。
    /// </remarks>
    private void OnCopy(object sender, RoutedEventArgs eventArgs) => _ = CopyAsync();

    private async Task CopyAsync()
    {
        // 剪貼簿被鎖住由 SqlClipboard 重試並回報；其他例外也不值得關掉視窗。
        try
        {
            var content = SqlTabularText.Build(CopyColumns(), _profileView.Cast<ResultGridColumnProfile>());
            var failure = await SqlClipboard.WriteAsync(SqlClipboard.CreateDataObject(content)).ConfigureAwait(true);
            _statusText.Text = failure ?? ResultGridWindowText.ProfileCopied(content.RowCount);
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"複製欄位剖析失敗：{exception.Message}");
            _statusText.Text = ResultGridWindowText.CopyFailed(exception.Message);
        }
    }
}
