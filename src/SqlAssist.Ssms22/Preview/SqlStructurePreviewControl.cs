using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SqlAssist.Core;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 浮動結構預覽的內容。
/// </summary>
/// <remarks>
/// 所有分頁都是一般的 WPF 控制項，選取、複製與焦點都是原生行為。
/// 這一點是刻意的：內嵌真正的編輯器雖然可以拿到免費的語法著色，
/// 但它會把鍵盤焦點搬進另一個呈現來源，編輯器因此判定自己失去聚合焦點，
/// 整個浮動視窗會在使用者點下去的那一刻被平台收掉。
/// 著色改由 <see cref="SqlScriptDocument"/> 自己排，顏色仍向編輯器借。
///
/// 複製一律走明確的處理常式與標題列按鈕，不依賴
/// <see cref="ApplicationCommands.Copy"/> 的繞送：浮動視窗裡的鍵盤焦點
/// 未必落在預期的元素上，命令繞送不到就會變成「選得起來但複製不了」。
/// </remarks>
internal sealed class SqlStructurePreviewControl : UserControl
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
    private readonly TabControl _tabs;
    private readonly TabItem _columnsTab;
    private readonly TabItem _indexesTab;
    private readonly TabItem _foreignKeysTab;
    private readonly TabItem _parametersTab;
    private readonly TabItem _scriptTab;
    private readonly DataGrid _columns;
    private readonly DataGrid _indexes;
    private readonly DataGrid _foreignKeys;
    private readonly DataGrid _parameters;
    private readonly RichTextBox _script;
    private readonly Border _root;

    /// <summary>已經填過內容的分頁；換了物件就整批清掉。</summary>
    private readonly HashSet<TabItem> _populated = new();

    /// <summary>目前顯示的結構；分頁按需填內容時要回頭讀它。</summary>
    private SqlObjectStructure? _structure;

    /// <summary>指令碼只組一次；複製與顯示都用同一份。</summary>
    private string? _scriptText;

    private SqlScriptDocument.Palette? _palette;

    public SqlStructurePreviewControl()
    {
        _title = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0),
            Foreground = VsThemeBrushes.ListForeground
        };

        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 3, 24, 4),
            Foreground = VsThemeBrushes.DimForeground
        };

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

        _script = new RichTextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = VsThemeBrushes.ListBackground,
            Foreground = VsThemeBrushes.ListForeground,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ContextMenu = CreateScriptMenu()
        };

        _columnsTab = new TabItem { Header = "欄位", Content = _columns };
        _indexesTab = new TabItem { Header = "索引", Content = _indexes };
        _foreignKeysTab = new TabItem { Header = "外來鍵", Content = _foreignKeys };
        _parametersTab = new TabItem { Header = "參數", Content = _parameters };
        _scriptTab = new TabItem { Header = "指令碼", Content = _script };

        _tabs = new TabControl
        {
            Background = VsThemeBrushes.ListBackground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        _tabs.Items.Add(_columnsTab);
        _tabs.Items.Add(_indexesTab);
        _tabs.Items.Add(_foreignKeysTab);
        _tabs.Items.Add(_parametersTab);
        _tabs.Items.Add(_scriptTab);
        _tabs.SelectionChanged += OnTabSelectionChanged;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        buttons.Children.Add(CreateButton("複製選取", CopySelection, "複製目前分頁選取的內容"));
        buttons.Children.Add(CreateButton("複製全部", CopyAll, "複製完整的 CREATE 指令碼"));

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 4, 4) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(_title);

        var resize = new Thumb
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Template = CreateResizeGripTemplate(),
            ToolTip = "拖曳調整大小；下次開啟會沿用"
        };
        resize.DragDelta += OnResizeDragDelta;
        resize.DragCompleted += (_, _) => SizeCommitted?.Invoke(this, EventArgs.Empty);

        var footer = new Grid();
        footer.Children.Add(_status);
        footer.Children.Add(resize);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_tabs, 1);
        Grid.SetRow(footer, 2);
        layout.Children.Add(header);
        layout.Children.Add(_tabs);
        layout.Children.Add(footer);

        _root = new Border
        {
            Background = VsThemeBrushes.ListBackground,
            BorderBrush = VsThemeBrushes.Border,
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = layout
        };

        Content = _root;

        // 顯示時不主動搶焦點：使用者還在打字，游標必須留在編輯器裡。
        // 點進來才接受焦點，那時才需要能夠拉選文字。
        Focusable = false;
    }

    /// <summary>使用者拖曳握把改變大小；每一次移動都要請平台重算位置。</summary>
    public event EventHandler? SizeChanging;

    /// <summary>拖曳結束，供呼叫端寫回設定。</summary>
    public event EventHandler? SizeCommitted;

    /// <summary>使用者在預覽裡按下 Esc。</summary>
    public event EventHandler? CloseRequested;

    public double PreferredWidth
    {
        get => _root.Width;
        set => _root.Width = value;
    }

    public double PreferredHeight
    {
        get => _root.Height;
        set => _root.Height = value;
    }

    /// <summary>換一個物件：標題先出來，內容等資料到齊。</summary>
    public void SetTarget(SqlObjectInfo objectInfo)
    {
        _structure = null;
        _scriptText = null;
        _populated.Clear();
        _title.Text = $"{objectInfo.Kind.ToDisplayName()}  {objectInfo.QualifiedName}";
        _status.Text = "載入中…";
        ClearTabs();
    }

    /// <summary>顯示一段訊息取代內容，例如沒有連線或這一項沒有結構。</summary>
    public void ShowMessage(string title, string message)
    {
        _structure = null;
        _scriptText = null;
        _populated.Clear();
        _title.Text = title;
        _status.Text = message;
        ClearTabs();
    }

    /// <summary>
    /// 先用第二層的欄位把畫面填起來。
    /// </summary>
    /// <remarks>
    /// 建議清單走過的物件，欄位早就在快取裡了。索引與外來鍵還要一次查詢，
    /// 但沒有理由讓已經拿得到的欄位陪著等——先畫欄位，其餘到齊再補。
    /// </remarks>
    public void PopulatePartial(SqlObjectDetail detail)
    {
        Populate(new SqlObjectStructure(detail), partial: true);
    }

    public void Populate(SqlObjectStructure structure)
    {
        Populate(structure, partial: false);
    }

    private void Populate(SqlObjectStructure structure, bool partial)
    {
        _structure = structure;
        _scriptText = null;
        _populated.Clear();

        // 空的分頁留在畫面上只會讓人多點一次才知道沒東西。
        _columnsTab.Visibility = Visible(structure.Columns.Count > 0);
        _indexesTab.Visibility = Visible(!partial && structure.Indexes.Count > 0);
        _foreignKeysTab.Visibility = Visible(!partial && structure.ForeignKeys.Count > 0);
        _parametersTab.Visibility = Visible(structure.Parameters.Count > 0);
        _scriptTab.Visibility = Visible(!partial);

        if (_tabs.SelectedItem is not TabItem selected || selected.Visibility != Visibility.Visible)
        {
            _tabs.SelectedItem = FirstVisibleTab();
        }

        PopulateSelectedTab();
        _status.Text = BuildSummary(structure, partial);
    }

    private TabItem? FirstVisibleTab()
    {
        foreach (TabItem tab in _tabs.Items)
        {
            if (tab.Visibility == Visibility.Visible)
            {
                return tab;
            }
        }

        return null;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        // 分頁裡的 DataGrid 換選取列時也會冒泡到這裡，那不是換分頁。
        if (!ReferenceEquals(eventArgs.OriginalSource, _tabs))
        {
            return;
        }

        PopulateSelectedTab();
    }

    /// <summary>
    /// 只填目前看得見的分頁。
    /// </summary>
    /// <remarks>
    /// 五個分頁一起填，等於每換一個物件就建立五份資料列與五次版面計算，
    /// 而使用者一次只看得到一個。切過去時再填，成本就落在他真的要看的那一次。
    /// </remarks>
    private void PopulateSelectedTab()
    {
        if (_structure is not { } structure || _tabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        if (!_populated.Add(tab))
        {
            return;
        }

        try
        {
            if (ReferenceEquals(tab, _columnsTab))
            {
                _columns.ItemsSource = Map(structure.Columns, column => new ColumnRow(column));
            }
            else if (ReferenceEquals(tab, _indexesTab))
            {
                _indexes.ItemsSource = Map(structure.Indexes, index => new IndexRow(index));
            }
            else if (ReferenceEquals(tab, _foreignKeysTab))
            {
                _foreignKeys.ItemsSource = Map(structure.ForeignKeys, key => new ForeignKeyRow(key));
            }
            else if (ReferenceEquals(tab, _parametersTab))
            {
                _parameters.ItemsSource = Map(structure.Parameters, parameter => new ParameterRow(parameter));
            }
            else if (ReferenceEquals(tab, _scriptTab))
            {
                _palette ??= SqlScriptDocument.CreatePalette();
                _script.Document = SqlScriptDocument.Build(GetScript(), _palette);
            }
        }
        catch (Exception exception)
        {
            _populated.Remove(tab);
            SqlAssistDiagnostics.WriteAlways($"填入預覽分頁失敗：{exception}");
            _status.Text = $"顯示失敗：{exception.Message}";
        }
    }

    private static List<TRow> Map<TSource, TRow>(IReadOnlyList<TSource> source, Func<TSource, TRow> convert)
    {
        var rows = new List<TRow>(source.Count);

        foreach (var item in source)
        {
            rows.Add(convert(item));
        }

        return rows;
    }

    private void ClearTabs()
    {
        _columns.ItemsSource = null;
        _indexes.ItemsSource = null;
        _foreignKeys.ItemsSource = null;
        _parameters.ItemsSource = null;
        _script.Document = new System.Windows.Documents.FlowDocument();
    }

    private string GetScript()
    {
        return _scriptText ??= _structure?.BuildScript() ?? string.Empty;
    }

    /// <summary>
    /// 複製目前分頁的選取內容。
    /// </summary>
    /// <remarks>
    /// 指令碼分頁沒有選取時複製整份，資料格沒有選取時什麼都不做——
    /// 資料格的「全部」是表格，使用者要的多半是指令碼，不該偷偷換一份東西給他。
    /// </remarks>
    public void CopySelection()
    {
        if (_tabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        if (ReferenceEquals(tab, _scriptTab))
        {
            var selected = _script.Selection?.Text;
            Copy(
                string.IsNullOrEmpty(selected) ? GetScript() : selected!,
                string.IsNullOrEmpty(selected) ? "沒有選取，已複製完整指令碼。" : "已複製選取的指令碼。");
            return;
        }

        if (tab.Content is DataGrid grid)
        {
            var text = BuildGridText(grid, selectedOnly: true);

            if (string.IsNullOrEmpty(text))
            {
                _status.Text = "請先在表格裡選取要複製的儲存格。";
                return;
            }

            Copy(text, "已複製選取的儲存格。");
        }
    }

    /// <summary>複製整份指令碼，與目前在哪個分頁無關。</summary>
    public void CopyAll()
    {
        Copy(GetScript(), "已複製完整指令碼到剪貼簿。");
    }

    private void CopyGridAll()
    {
        if (_tabs.SelectedItem is TabItem { Content: DataGrid grid })
        {
            Copy(BuildGridText(grid, selectedOnly: false), "已複製整個表格。");
        }
    }

    /// <summary>
    /// 自己把資料格排成定位字元分隔的文字。
    /// </summary>
    /// <remarks>
    /// 不用 <see cref="DataGrid"/> 內建的複製命令：那條路要求資料格持有鍵盤焦點，
    /// 而浮動視窗裡的焦點未必在那裡，結果就是選單項目變成灰的、Ctrl+C 沒有反應。
    /// 自己組文字則不管焦點在哪都成立。
    /// </remarks>
    private static string BuildGridText(DataGrid grid, bool selectedOnly)
    {
        var builder = new StringBuilder();
        var rows = new List<object>();

        if (selectedOnly)
        {
            foreach (var cell in grid.SelectedCells)
            {
                if (cell.Item is { } item && !rows.Contains(item))
                {
                    rows.Add(item);
                }
            }
        }
        else if (grid.ItemsSource is IEnumerable<object> items)
        {
            rows.AddRange(items);
        }

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = new List<DataGridColumn>();

        foreach (var column in grid.Columns)
        {
            // 只選了幾欄時就只複製那幾欄，這正是以儲存格為選取單位的用意。
            if (!selectedOnly || IsColumnSelected(grid, column))
            {
                columns.Add(column);
            }
        }

        AppendLine(builder, columns, column => column.Header?.ToString() ?? string.Empty);

        foreach (var row in rows)
        {
            AppendLine(builder, columns, column => GetCellText(column, row));
        }

        return builder.ToString();
    }

    private static bool IsColumnSelected(DataGrid grid, DataGridColumn column)
    {
        foreach (var cell in grid.SelectedCells)
        {
            if (ReferenceEquals(cell.Column, column))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendLine(
        StringBuilder builder,
        List<DataGridColumn> columns,
        Func<DataGridColumn, string> select)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('\t');
            }

            builder.Append(select(columns[index]));
        }

        builder.AppendLine();
    }

    private static string GetCellText(DataGridColumn column, object row)
    {
        if (column is not DataGridTextColumn { Binding: Binding binding } ||
            binding.Path?.Path is not { Length: > 0 } path)
        {
            return string.Empty;
        }

        var value = row.GetType().GetProperty(path)?.GetValue(row);
        return value?.ToString() ?? string.Empty;
    }

    private void Copy(string text, string successMessage)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _status.Text = successMessage;
        }
        catch (Exception exception)
        {
            // 剪貼簿被別的程序鎖住時會擲例外，這不值得中斷預覽。
            SqlAssistDiagnostics.WriteAlways($"複製預覽內容失敗：{exception.Message}");
            _status.Text = $"複製失敗：{exception.Message}";
        }
    }

    private ContextMenu CreateScriptMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "複製選取內容" };
        copy.Click += (_, _) => CopySelection();
        var copyAll = new MenuItem { Header = "複製完整指令碼" };
        copyAll.Click += (_, _) => CopyAll();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        return menu;
    }

    private ContextMenu CreateGridMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "複製選取的儲存格" };
        copy.Click += (_, _) => CopySelection();
        var copyAll = new MenuItem { Header = "複製整個表格" };
        copyAll.Click += (_, _) => CopyGridAll();
        var copyScript = new MenuItem { Header = "複製完整指令碼" };
        copyScript.Click += (_, _) => CopyAll();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        menu.Items.Add(copyScript);
        return menu;
    }

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        _root.Width = Clamp(
            _root.Width + eventArgs.HorizontalChange,
            SqlAssistPreviewSettings.MinimumWidth,
            SqlAssistPreviewSettings.MaximumWidth);

        _root.Height = Clamp(
            _root.Height + eventArgs.VerticalChange,
            SqlAssistPreviewSettings.MinimumHeight,
            SqlAssistPreviewSettings.MaximumHeight);

        // 不通知平台的話，視窗會維持在原本算好的位置往外長，
        // 貼在左側時看起來就變成「左邊界被往外拉」。
        SizeChanging?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 套用尺寸，但不超過編輯器目前看得到的範圍。
    /// </summary>
    /// <remarks>
    /// 每次顯示都從設定值重新算，而不是把現有寬度再壓一次：
    /// 後者會讓視窗在一個窄的查詢視窗裡被縮小之後，換到寬的視窗也長不回來。
    /// </remarks>
    public void ApplySize(double width, double height, double availableWidth, double availableHeight)
    {
        _root.Width = availableWidth > SqlAssistPreviewSettings.MinimumWidth
            ? Math.Min(width, availableWidth)
            : width;

        _root.Height = availableHeight > SqlAssistPreviewSettings.MinimumHeight
            ? Math.Min(height, availableHeight)
            : height;
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(Math.Max(value, minimum), maximum);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs eventArgs)
    {
        // 焦點在預覽裡時，編輯器的命令處理常式收不到按鍵，這兩個得由這裡處理。
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (eventArgs.Key == Key.C &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            eventArgs.Handled = true;
            CopySelection();
            return;
        }

        base.OnPreviewKeyDown(eventArgs);
    }

    private static string BuildSummary(SqlObjectStructure structure, bool partial)
    {
        var builder = new StringBuilder();

        if (structure.Columns.Count > 0)
        {
            builder.Append(structure.Columns.Count).Append(" 個欄位");
        }

        if (structure.Parameters.Count > 0)
        {
            Separate(builder);
            builder.Append(structure.Parameters.Count).Append(" 個參數");
        }

        if (partial)
        {
            Separate(builder);
            builder.Append("索引與外來鍵載入中…");
            return builder.ToString();
        }

        if (structure.PrimaryKey is { } primaryKey)
        {
            Separate(builder);
            builder.Append("PK：").Append(primaryKey.DescribeKeyColumns());
        }
        else if (structure.Object.Kind == SqlObjectKind.Table)
        {
            Separate(builder);
            builder.Append("沒有主索引鍵");
        }

        if (structure.Indexes.Count > 0)
        {
            Separate(builder);
            builder.Append(structure.Indexes.Count).Append(" 個索引");
        }

        if (structure.ForeignKeys.Count > 0)
        {
            Separate(builder);
            builder.Append(structure.ForeignKeys.Count).Append(" 個外來鍵");
        }

        return builder.Length == 0 ? structure.Object.Kind.ToDisplayName() : builder.ToString();
    }

    private static void Separate(StringBuilder builder)
    {
        if (builder.Length > 0)
        {
            builder.Append("　");
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Button CreateButton(string text, Action click, string tooltip)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(10, 2, 10, 2),
            ToolTip = tooltip,

            // 按鈕不吃焦點：按一下複製之後，焦點該留在原本選取的地方。
            Focusable = false
        };

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// 右下角的縮放握把。
    /// </summary>
    /// <remarks>
    /// 自己畫三條斜線而不是用 <see cref="ResizeGrip"/>：後者的預設樣式假設自己在
    /// 視窗的狀態列裡，放在浮動視窗上不一定畫得出來。
    /// </remarks>
    private static ControlTemplate CreateResizeGripTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));

        // 透明底色讓整個 16×16 都吃得到滑鼠，只有線條本身可以拖曳會很難點。
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var lines = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        lines.SetValue(
            System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M 2,14 L 14,2 M 6,14 L 14,6 M 10,14 L 14,10"));
        lines.SetValue(System.Windows.Shapes.Path.StrokeProperty, VsThemeBrushes.DimForeground);
        lines.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.0);
        lines.SetValue(IsHitTestVisibleProperty, false);
        root.AppendChild(lines);

        template.VisualTree = root;
        return template;
    }

    /// <summary>
    /// 建立唯讀資料格。
    /// </summary>
    /// <remarks>
    /// 以儲存格為選取單位，使用者才能只拉走要的那幾欄；
    /// 複製走自己的處理常式，不依賴內建命令的繞送。
    /// </remarks>
    private DataGrid CreateGrid(params (string Header, string Path)[] columns)
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
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = VsThemeBrushes.ListBackground,
            Foreground = VsThemeBrushes.ListForeground,
            RowBackground = VsThemeBrushes.ListBackground,
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ContextMenu = CreateGridMenu()
        };

        foreach (var (header, path) in columns)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = DataGridLength.Auto
            });
        }

        return grid;
    }
}
