using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.Structure;

/// <summary>
/// 結構面板的內容。
/// </summary>
/// <remarks>
/// 滑鼠停留提示受限於提示視窗：不能捲動、不能選取、欄位多就一定看不完。
/// 這個面板補上另一半——可停駐、可捲動、可以用滑鼠拉選再複製，
/// 而且索引、外來鍵與完整指令碼都在同一個地方。
/// </remarks>
internal sealed class SqlObjectStructureControl : UserControl
{
    private sealed class ColumnRow
    {
        public ColumnRow(SqlColumnInfo column)
        {
            Ordinal = column.Ordinal;
            Name = column.Name;
            DataType = column.DataType;
            Nullable = column.IsNullable ? "NULL" : "NOT NULL";
            PrimaryKey = column.IsPrimaryKey ? "PK" : string.Empty;
            Identity = column.IsIdentity ? "IDENTITY" : string.Empty;
            Computed = column.IsComputed ? column.ComputedDefinition ?? "COMPUTED" : string.Empty;
            Default = column.DefaultDefinition ?? string.Empty;
        }

        public int Ordinal { get; }

        public string Name { get; }

        public string DataType { get; }

        public string Nullable { get; }

        public string PrimaryKey { get; }

        public string Identity { get; }

        public string Computed { get; }

        public string Default { get; }
    }

    private sealed class IndexRow
    {
        public IndexRow(SqlIndexInfo index)
        {
            Name = index.Name;
            Kind = index.DescribeKind();
            KeyColumns = index.DescribeKeyColumns();
            IncludedColumns = index.DescribeIncludedColumns();
            Filter = index.FilterDefinition ?? string.Empty;
        }

        public string Name { get; }

        public string Kind { get; }

        public string KeyColumns { get; }

        public string IncludedColumns { get; }

        public string Filter { get; }
    }

    private sealed class ForeignKeyRow
    {
        public ForeignKeyRow(SqlForeignKeyInfo foreignKey)
        {
            Name = foreignKey.Name;
            Columns = foreignKey.DescribeColumns();
            Actions = foreignKey.DescribeActions();
        }

        public string Name { get; }

        public string Columns { get; }

        public string Actions { get; }
    }

    private sealed class ParameterRow
    {
        public ParameterRow(SqlParameterInfo parameter)
        {
            Name = parameter.Name;
            DataType = parameter.DataType;
            Direction = parameter.IsOutput ? "OUTPUT" : string.Empty;
        }

        public string Name { get; }

        public string DataType { get; }

        public string Direction { get; }
    }

    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly Button _refresh;
    private readonly Button _copyScript;
    private readonly TabControl _tabs;
    private readonly DataGrid _columns;
    private readonly DataGrid _indexes;
    private readonly DataGrid _foreignKeys;
    private readonly DataGrid _parameters;
    private readonly TextBox _script;
    private readonly TabItem _columnsTab;
    private readonly TabItem _indexesTab;
    private readonly TabItem _foreignKeysTab;
    private readonly TabItem _parametersTab;

    /// <summary>跟隨建議清單選取時的緩衝：選取停下來才真的去查資料庫。</summary>
    private readonly DispatcherTimer _followTimer;

    private SqlObjectInfo? _objectInfo;
    private SqlMetadataService? _metadataService;
    private CancellationTokenSource? _loading;

    public SqlObjectStructureControl()
    {
        _title = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Foreground = VsThemeBrushes.WindowForeground,
            Text = "尚未選取物件"
        };

        _status = new TextBlock
        {
            Margin = new Thickness(8, 4, 8, 4),
            Foreground = VsThemeBrushes.DimForeground,
            Text = "在編輯器裡把滑鼠停在物件名稱上，點提示裡的「開啟完整結構」。"
        };

        _refresh = CreateButton("重新整理", () => Reload(force: true));
        _copyScript = CreateButton("複製指令碼", CopyScript);

        _columns = CreateGrid(
            ("#", nameof(ColumnRow.Ordinal)),
            ("欄位", nameof(ColumnRow.Name)),
            ("型別", nameof(ColumnRow.DataType)),
            ("NULL", nameof(ColumnRow.Nullable)),
            ("PK", nameof(ColumnRow.PrimaryKey)),
            ("IDENTITY", nameof(ColumnRow.Identity)),
            ("計算欄位", nameof(ColumnRow.Computed)),
            ("預設值", nameof(ColumnRow.Default)));

        _indexes = CreateGrid(
            ("索引", nameof(IndexRow.Name)),
            ("種類", nameof(IndexRow.Kind)),
            ("索引鍵", nameof(IndexRow.KeyColumns)),
            ("INCLUDE", nameof(IndexRow.IncludedColumns)),
            ("篩選", nameof(IndexRow.Filter)));

        _foreignKeys = CreateGrid(
            ("外來鍵", nameof(ForeignKeyRow.Name)),
            ("參考", nameof(ForeignKeyRow.Columns)),
            ("動作", nameof(ForeignKeyRow.Actions)));

        _parameters = CreateGrid(
            ("參數", nameof(ParameterRow.Name)),
            ("型別", nameof(ParameterRow.DataType)),
            ("方向", nameof(ParameterRow.Direction)));

        _script = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"),
            BorderThickness = new Thickness(0),
            Background = VsThemeBrushes.WindowBackground,
            Foreground = VsThemeBrushes.WindowForeground,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        _columnsTab = new TabItem { Header = "欄位", Content = _columns };
        _indexesTab = new TabItem { Header = "索引", Content = _indexes };
        _foreignKeysTab = new TabItem { Header = "外來鍵", Content = _foreignKeys };
        _parametersTab = new TabItem { Header = "參數", Content = _parameters };

        _tabs = new TabControl
        {
            Background = VsThemeBrushes.WindowBackground,
            BorderThickness = new Thickness(0)
        };
        _tabs.Items.Add(_columnsTab);
        _tabs.Items.Add(_indexesTab);
        _tabs.Items.Add(_foreignKeysTab);
        _tabs.Items.Add(_parametersTab);
        _tabs.Items.Add(new TabItem { Header = "指令碼", Content = _script });

        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_copyScript);
        DockPanel.SetDock(buttons, Dock.Right);
        toolbar.Children.Add(buttons);
        toolbar.Children.Add(_title);

        var root = new DockPanel { LastChildFill = true, Background = VsThemeBrushes.WindowBackground };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(_tabs);

        _followTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _followTimer.Tick += OnFollowTimerTick;

        Content = root;
        Background = VsThemeBrushes.WindowBackground;
        Foreground = VsThemeBrushes.WindowForeground;
        SetButtonsEnabled(false);
    }

    /// <summary>換一個物件並立刻載入它的結構；使用者主動要求時走這條路。</summary>
    public void Show(SqlObjectInfo objectInfo, SqlMetadataService metadataService)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _followTimer.Stop();
        SetTarget(objectInfo, metadataService);
        Reload(force: false);
    }

    /// <summary>
    /// 跟著建議清單的選取換一個物件。
    /// </summary>
    /// <remarks>
    /// 使用者用方向鍵掃過 20 個資料表時，不能就這樣送出 20 次查詢。
    /// 因此：快取裡有就立刻畫出來（不查、不閃動），沒有的話先顯示標題與載入中，
    /// 等選取停下來才真的去查——停在某一項上看，才是真的想看它。
    /// </remarks>
    public void Follow(SqlObjectInfo objectInfo, SqlMetadataService metadataService)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_objectInfo is { } current && current.ObjectId == objectInfo.ObjectId)
        {
            return;
        }

        _followTimer.Stop();
        SetTarget(objectInfo, metadataService);

        if (metadataService.PeekStructure(objectInfo) is { } cached)
        {
            _loading?.Cancel();
            Populate(objectInfo, cached);
            return;
        }

        _loading?.Cancel();
        SetButtonsEnabled(false);
        _status.Text = "載入中…";
        _followTimer.Start();
    }

    private void OnFollowTimerTick(object? sender, EventArgs eventArgs)
    {
        _followTimer.Stop();
        Reload(force: false);
    }

    private void SetTarget(SqlObjectInfo objectInfo, SqlMetadataService metadataService)
    {
        _objectInfo = objectInfo;
        _metadataService = metadataService;
        _title.Text = $"{objectInfo.Kind.ToDisplayName()} {objectInfo.QualifiedName}";
    }

    private void Reload(bool force)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_objectInfo is not { } objectInfo || _metadataService is not { } metadataService)
        {
            return;
        }

        // 上一個物件還在載入時直接放棄它：使用者已經在看別的東西了。
        _loading?.Cancel();
        _loading?.Dispose();
        var source = new CancellationTokenSource();
        _loading = source;

        SetButtonsEnabled(false);
        _status.Text = "載入中…";

        if (force)
        {
            // 只丟掉這一個物件：使用者要的是「這張表」，
            // 沒有理由讓整個資料庫的物件清單跟著重來一次。
            metadataService.InvalidateObject(objectInfo);
        }

        _ = LoadAsync(objectInfo, metadataService, source.Token);
    }

    private async Task LoadAsync(
        SqlObjectInfo objectInfo,
        SqlMetadataService metadataService,
        CancellationToken cancellationToken)
    {
        try
        {
            var structure = await metadataService
                .GetStructureAsync(objectInfo, cancellationToken)
                .ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Populate(objectInfo, structure);
        }
        catch (OperationCanceledException)
        {
            // 換了物件或關掉面板，什麼都不用做。
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"載入物件結構失敗：{exception}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _status.Text = $"載入失敗：{exception.Message}";
            SetButtonsEnabled(true);
        }
    }

    private void Populate(SqlObjectInfo objectInfo, SqlObjectStructure? structure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (structure is null)
        {
            _status.Text = "沒有可用的連線；請先在查詢視窗連上資料庫。";
            SetButtonsEnabled(true);
            return;
        }

        var columns = new List<ColumnRow>(structure.Columns.Count);

        foreach (var column in structure.Columns)
        {
            columns.Add(new ColumnRow(column));
        }

        var indexes = new List<IndexRow>(structure.Indexes.Count);

        foreach (var index in structure.Indexes)
        {
            indexes.Add(new IndexRow(index));
        }

        var foreignKeys = new List<ForeignKeyRow>(structure.ForeignKeys.Count);

        foreach (var foreignKey in structure.ForeignKeys)
        {
            foreignKeys.Add(new ForeignKeyRow(foreignKey));
        }

        var parameters = new List<ParameterRow>(structure.Parameters.Count);

        foreach (var parameter in structure.Parameters)
        {
            parameters.Add(new ParameterRow(parameter));
        }

        _columns.ItemsSource = columns;
        _indexes.ItemsSource = indexes;
        _foreignKeys.ItemsSource = foreignKeys;
        _parameters.ItemsSource = parameters;
        _script.Text = structure.BuildScript();

        // 空的分頁留在畫面上只會讓人多點一次才知道沒東西。
        _columnsTab.Visibility = Visible(columns.Count > 0);
        _indexesTab.Visibility = Visible(indexes.Count > 0);
        _foreignKeysTab.Visibility = Visible(foreignKeys.Count > 0);
        _parametersTab.Visibility = Visible(parameters.Count > 0);

        if (_tabs.SelectedItem is TabItem selected && selected.Visibility != Visibility.Visible)
        {
            _tabs.SelectedIndex = 0;
        }

        _status.Text = BuildSummary(structure);
        SetButtonsEnabled(true);
    }

    private static string BuildSummary(SqlObjectStructure structure)
    {
        var builder = new StringBuilder();
        builder.Append(structure.Object.Kind.ToDisplayName()).Append("  ");

        if (structure.Columns.Count > 0)
        {
            builder.Append(structure.Columns.Count).Append(" 個欄位");
        }

        if (structure.PrimaryKey is { } primaryKey)
        {
            builder.Append("　PK：").Append(primaryKey.DescribeKeyColumns());
        }
        else if (structure.Object.Kind == SqlObjectKind.Table)
        {
            builder.Append("　沒有主索引鍵");
        }

        if (structure.Indexes.Count > 0)
        {
            builder.Append("　").Append(structure.Indexes.Count).Append(" 個索引");
        }

        if (structure.ForeignKeys.Count > 0)
        {
            builder.Append("　").Append(structure.ForeignKeys.Count).Append(" 個外來鍵");
        }

        return builder.ToString();
    }

    private void CopyScript()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (string.IsNullOrEmpty(_script.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_script.Text);
            _status.Text = "已複製完整指令碼到剪貼簿。";
        }
        catch (Exception exception)
        {
            // 剪貼簿被別的程序鎖住時會擲例外，這不值得中斷面板。
            SqlAssistDiagnostics.WriteAlways($"複製指令碼失敗：{exception.Message}");
            _status.Text = $"複製失敗：{exception.Message}";
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _refresh.IsEnabled = enabled && _objectInfo is not null;
        _copyScript.IsEnabled = enabled && _objectInfo is not null;
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Button CreateButton(string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(10, 2, 10, 2),
            MinWidth = 84
        };

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// 建立唯讀資料格。
    /// </summary>
    /// <remarks>
    /// 以儲存格為選取單位並開啟含標題的剪貼簿複製，使用者才能只拉走要的那幾欄；
    /// 這正是提示視窗做不到、而這個面板存在的理由。
    /// </remarks>
    private static DataGrid CreateGrid(params (string Header, string Path)[] columns)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            HeadersVisibility = DataGridHeadersVisibility.All,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = VsThemeBrushes.WindowBackground,
            Foreground = VsThemeBrushes.WindowForeground,
            RowBackground = VsThemeBrushes.WindowBackground,
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        foreach (var (header, path) in columns)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(path),
                Width = DataGridLength.Auto
            });
        }

        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "複製選取內容", Command = ApplicationCommands.Copy };
        var copyAll = new MenuItem { Header = "全選並複製" };
        copyAll.Click += (_, _) =>
        {
            grid.SelectAllCells();
            ApplicationCommands.Copy.Execute(null, grid);
        };
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        grid.ContextMenu = menu;

        return grid;
    }
}
