using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Metadata.ResultGrid;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.ResultGrid;

/// <summary>
/// 每一欄的統計摘要，一欄一列。
/// </summary>
/// <remarks>
/// 用視窗而不是產指令碼：這份東西的用途是<b>看</b>，不是貼。178 欄的摘要塞進查詢
/// 視窗變成 178 行註解，比原本捲格線還難讀；擺成一張表才排得出「整欄都是 NULL 的
/// 那幾欄」。要帶走的人按「複製」拿 TSV，貼進哪裡都能排。
///
/// 外觀全部走 <see cref="SqlAssistChrome"/>，一個樣式都不自己定義——
/// 這是自製 UI 準則的「禁止在 UI/SqlAssistChrome 之外另立一套外觀」。
/// </remarks>
internal sealed class ResultGridProfileWindow : DialogWindow
{
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    private readonly IReadOnlyList<ResultGridColumnProfile> _profiles;
    private readonly TextBlock _statusText;

    public ResultGridProfileWindow(ResultGridTable table, IReadOnlyList<ResultGridColumnProfile> profiles)
    {
        VsThemeBrushes.Apply(this);
        _profiles = profiles;

        Title = "SqlAssist — 欄位剖析";
        Width = 900;
        Height = 620;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, ThemeBrush.WindowBackground);
        SetResourceReference(ForegroundProperty, ThemeBrush.WindowForeground);
        FontFamily = SqlAssistChrome.InterfaceFont;
        FontSize = Metrics.Body;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _statusText = SqlAssistChrome.CreateStatusText(Metrics);
        Content = BuildLayout(table);
    }

    private Grid BuildLayout(ResultGridTable table)
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = SqlAssistChrome.CreateLabel(Describe(table), Metrics);
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var surface = SqlAssistChrome.CreateSurface(CreateGrid());
        surface.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(surface, 1);
        root.Children.Add(surface);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };

        var copy = SqlAssistChrome.CreateButton("複製成表格", Metrics);
        copy.MinWidth = 78;
        copy.Margin = new Thickness(0, 0, 6, 0);
        copy.Click += OnCopy;

        var close = SqlAssistChrome.CreateButton("關閉", Metrics, primary: true);
        close.MinWidth = 78;
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        DockPanel.SetDock(copy, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(copy);
        footer.Children.Add(close);
        footer.Children.Add(_statusText);

        Grid.SetRow(footer, 2);
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
        grid.ItemsSource = _profiles;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

        var cellText = SqlAssistChrome.CreateCellTextStyle();

        Add(grid, "欄位", nameof(ResultGridColumnProfile.Name), 180, cellText);
        Add(grid, "型別", nameof(ResultGridColumnProfile.DataType), 130, cellText);
        Add(grid, "NULL", nameof(ResultGridColumnProfile.NullCount), 64, cellText);
        Add(grid, "空字串", nameof(ResultGridColumnProfile.EmptyTextCount), 64, cellText);
        Add(grid, "相異", nameof(ResultGridColumnProfile.DistinctCount), 64, cellText);
        Add(grid, "長度", nameof(ResultGridColumnProfile.TextLength), 72, cellText);
        Add(grid, "最小", nameof(ResultGridColumnProfile.Minimum), 150, cellText);
        Add(grid, "最大", nameof(ResultGridColumnProfile.Maximum), 1, cellText, star: true);

        return grid;
    }

    private static void Add(
        DataGrid grid,
        string header,
        string property,
        double width,
        Style cellText,
        bool star = false)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            IsReadOnly = true,
            ElementStyle = cellText,
            Width = star
                ? new DataGridLength(width, DataGridLengthUnitType.Star)
                : new DataGridLength(width)
        });
    }

    private static string Describe(ResultGridTable table) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} 欄 × {1} 列{2}，資料取自結果格線，沒有重新查詢資料庫。",
            table.Columns.Count,
            table.Rows.Count,
            table.IsWholeResult ? "（整份結果）" : "（選取範圍）");

    /// <remarks>
    /// 複製成以 Tab 分隔的表格，帶標題列——貼進 Excel、Markdown 表格產生器或
    /// 另一個查詢視窗都排得開。這裡不另外做匯出格式的選項：多一種格式就多一份
    /// 要跟著欄位改的東西，而 TSV 是唯一貼到哪裡都認得的。
    /// </remarks>
    private void OnCopy(object sender, RoutedEventArgs eventArgs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("欄位\t型別\tNULL\t空字串\t相異\t長度\t最小\t最大");

        foreach (var profile in _profiles)
        {
            builder.Append(profile.Name).Append('\t')
                .Append(profile.DataType).Append('\t')
                .Append(profile.NullCount.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(profile.EmptyTextCount.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(profile.DistinctCount.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(profile.TextLength).Append('\t')
                .Append(profile.Minimum).Append('\t')
                .AppendLine(profile.Maximum);
        }

        try
        {
            Clipboard.SetText(builder.ToString());
            _statusText.Text = "已複製 " + _profiles.Count.ToString(CultureInfo.InvariantCulture) + " 欄的摘要。";
        }
        catch (Exception exception)
        {
            // 剪貼簿被別的程序鎖住時會擲例外，這不值得關掉視窗。
            SqlAssistDiagnostics.WriteAlways($"複製欄位剖析失敗：{exception.Message}");
            _statusText.Text = $"複製失敗：{exception.Message}";
        }
    }
}
