using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 浮動結構預覽的內容。
/// </summary>
/// <remarks>
/// 指令碼分頁裡放的是一個真正的唯讀編輯器，不是 TextBox：
/// 語法著色、滑鼠拉選、捲動與尋找都由編輯器自己處理，外觀也就與查詢視窗一致。
/// 它不在 SSMS 的命令繞送鏈上，因此鍵盤打不進去（天然唯讀），
/// 但 Ctrl+C 同樣送不到它——那一段由 <see cref="CopySelection"/> 補上。
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
    private readonly ContentControl _scriptSlot;
    private readonly Border _root;

    /// <summary>已經填過內容的分頁；換了物件就整批清掉。</summary>
    private readonly HashSet<TabItem> _populated = new();

    private IWpfTextViewHost? _scriptHost;
    private IWpfTextView? _scriptView;
    private ITextBuffer? _scriptBuffer;
    private IReadOnlyRegion? _scriptReadOnly;
    private TextBox? _scriptFallback;

    /// <summary>目前顯示的結構；分頁按需填內容時要回頭讀它。</summary>
    private SqlObjectStructure? _structure;

    public SqlStructurePreviewControl()
    {
        _title = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 5, 8, 4),
            Foreground = VsThemeBrushes.ListForeground
        };

        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 3, 22, 4),
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

        _scriptSlot = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        _columnsTab = new TabItem { Header = "欄位", Content = _columns };
        _indexesTab = new TabItem { Header = "索引", Content = _indexes };
        _foreignKeysTab = new TabItem { Header = "外來鍵", Content = _foreignKeys };
        _parametersTab = new TabItem { Header = "參數", Content = _parameters };
        _scriptTab = new TabItem { Header = "指令碼", Content = _scriptSlot };

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
        Grid.SetRow(_title, 0);
        Grid.SetRow(_tabs, 1);
        Grid.SetRow(footer, 2);
        layout.Children.Add(_title);
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
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyCommand, OnCanCopy));
    }

    /// <summary>使用者拖曳握把改變大小之後引發，供呼叫端寫回設定。</summary>
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
        _populated.Clear();
        _title.Text = $"{objectInfo.Kind.ToDisplayName()}  {objectInfo.QualifiedName}";
        _status.Text = "載入中…";
        ClearTabs();
    }

    /// <summary>顯示一段訊息取代內容，例如沒有連線或這一項沒有結構。</summary>
    public void ShowMessage(string title, string message)
    {
        _structure = null;
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
                SetScript(structure.BuildScript());
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
    }

    /// <summary>目前選取的文字；沒有選取時是整份指令碼。</summary>
    private string GetCopyText()
    {
        if (_scriptView is { } view && !view.Selection.IsEmpty)
        {
            return view.Selection.StreamSelectionSpan.GetText();
        }

        return _structure?.BuildScript() ?? string.Empty;
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs eventArgs)
    {
        eventArgs.CanExecute = _structure is not null || _scriptView is not null;
        eventArgs.Handled = true;
    }

    private void OnCopyCommand(object sender, ExecutedRoutedEventArgs eventArgs)
    {
        CopySelection();
        eventArgs.Handled = true;
    }

    /// <summary>
    /// 把選取的內容放進剪貼簿。
    /// </summary>
    /// <remarks>
    /// 內嵌的編輯器不在 SSMS 的命令繞送鏈上，Ctrl+C 不會自動送達它，
    /// 因此由這裡接手：有選取就複製選取，沒有就複製整份指令碼。
    /// 分頁裡的資料格有自己的複製命令，會先處理掉，不會走到這裡。
    /// </remarks>
    public void CopySelection()
    {
        Copy(GetCopyText(), "已複製到剪貼簿。");
    }

    /// <summary>複製整份指令碼，忽略目前的選取。</summary>
    public void CopyAll()
    {
        Copy(_structure?.BuildScript() ?? string.Empty, "已複製完整指令碼到剪貼簿。");
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

    /// <summary>
    /// 建立內嵌的唯讀編輯器。
    /// </summary>
    /// <remarks>
    /// 只在第一次要顯示指令碼時建立：使用者可能整場都只看欄位分頁，
    /// 沒有理由為此先付一次建立編輯器的成本。
    /// </remarks>
    private void EnsureScriptView()
    {
        if (_scriptHost is not null || _scriptFallback is not null)
        {
            return;
        }

        if (SqlPreviewServices.Current is not { } services)
        {
            _scriptSlot.Content = CreateFallbackScriptBox();
            return;
        }

        try
        {
            _scriptBuffer = services.BufferFactory.CreateTextBuffer(
                string.Empty,
                services.GetPreviewContentType());

            // 只要 INTERACTIVE：滑鼠拉選的處理常式綁在這個角色上。
            // 刻意不要 EDITABLE——本擴充的建議來源與提示來源都限定該角色，
            // 少了它，預覽視窗就不可能反過來觸發自己的 IntelliSense。
            var roles = services.EditorFactory.CreateTextViewRoleSet(PredefinedTextViewRoles.Interactive);
            _scriptView = services.EditorFactory.CreateTextView(_scriptBuffer, roles);

            var options = _scriptView.Options;
            options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.SelectionMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.ZoomControlId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.SuggestionMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.VerticalScrollBarId, true);
            options.SetOptionValue(DefaultTextViewHostOptions.HorizontalScrollBarId, true);
            options.SetOptionValue(DefaultTextViewOptions.DragDropEditingId, false);
            options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);

            _scriptHost = services.EditorFactory.CreateTextViewHost(_scriptView, setFocus: false);
            _scriptHost.HostControl.ContextMenu = CreateScriptMenu();
            _scriptSlot.Content = _scriptHost.HostControl;
        }
        catch (Exception exception)
        {
            // 內嵌編輯器建不起來時退回純文字：少了著色，但指令碼仍然看得到也複製得走。
            SqlAssistDiagnostics.WriteAlways($"建立內嵌指令碼編輯器失敗：{exception}");
            _scriptHost = null;
            _scriptView = null;
            _scriptBuffer = null;
            _scriptSlot.Content = CreateFallbackScriptBox();
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

    private TextBox CreateFallbackScriptBox()
    {
        _scriptFallback = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas"),
            BorderThickness = new Thickness(0),
            Background = VsThemeBrushes.ListBackground,
            Foreground = VsThemeBrushes.ListForeground,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        return _scriptFallback;
    }

    private void SetScript(string script)
    {
        EnsureScriptView();

        if (_scriptView is null || _scriptBuffer is null)
        {
            if (_scriptFallback is { } fallback)
            {
                fallback.Text = script;
            }

            return;
        }

        // 唯讀區段要先解除才改得動內容；改完再重新蓋上。
        if (_scriptReadOnly is { } region)
        {
            using var unlock = _scriptBuffer.CreateReadOnlyRegionEdit();
            unlock.RemoveReadOnlyRegion(region);
            unlock.Apply();
            _scriptReadOnly = null;
        }

        using (var edit = _scriptBuffer.CreateEdit())
        {
            edit.Replace(0, _scriptBuffer.CurrentSnapshot.Length, script);
            edit.Apply();
        }

        using (var relock = _scriptBuffer.CreateReadOnlyRegionEdit())
        {
            _scriptReadOnly = relock.CreateReadOnlyRegion(
                new Span(0, _scriptBuffer.CurrentSnapshot.Length));
            relock.Apply();
        }

        var snapshot = _scriptBuffer.CurrentSnapshot;
        _scriptView.Caret.MoveTo(new SnapshotPoint(snapshot, 0));
        _scriptView.Selection.Clear();
        _scriptView.DisplayTextLineContainingBufferPosition(
            new SnapshotPoint(snapshot, 0),
            0,
            ViewRelativePosition.Top);
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
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Min(Math.Max(value, minimum), maximum);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs eventArgs)
    {
        // 焦點在預覽裡時，編輯器的命令處理常式收不到按鍵，Esc 得由這裡處理。
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// 右下角的縮放握把。
    /// </summary>
    /// <remarks>
    /// 自己畫三條斜線而不是用 <see cref="ResizeGrip"/>：後者的預設樣式假設自己在
    /// 視窗的狀態列裡，放在浮動視窗上不一定畫得出來。三個圖形永遠是三個圖形。
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
    /// 趁閒置時把內嵌編輯器也先建好。
    /// </summary>
    /// <remarks>
    /// 這是整套裡最貴的一步，留到使用者第一次點「指令碼」分頁才做，
    /// 就等於在那一次點擊上卡一下。
    /// </remarks>
    public void Warmup()
    {
        EnsureScriptView();
    }

    /// <summary>
    /// 建立唯讀資料格。
    /// </summary>
    /// <remarks>
    /// 以儲存格為選取單位並開啟含標題的剪貼簿複製，使用者才能只拉走要的那幾欄。
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
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = VsThemeBrushes.ListBackground,
            Foreground = VsThemeBrushes.ListForeground,
            RowBackground = VsThemeBrushes.ListBackground,
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
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

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "複製選取內容", Command = ApplicationCommands.Copy });
        var copyAll = new MenuItem { Header = "全選並複製" };
        copyAll.Click += (_, _) =>
        {
            grid.SelectAllCells();
            ApplicationCommands.Copy.Execute(null, grid);
        };
        menu.Items.Add(copyAll);
        grid.ContextMenu = menu;

        return grid;
    }

    /// <summary>關閉內嵌編輯器；編輯器關閉時必須連帶釋放，否則會留下平台的訂閱。</summary>
    public void Close()
    {
        try
        {
            _scriptHost?.Close();
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.Write($"關閉內嵌指令碼編輯器失敗：{exception.Message}");
        }
        finally
        {
            _scriptHost = null;
            _scriptView = null;
            _scriptBuffer = null;
            _scriptReadOnly = null;
        }
    }
}
