using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Preview;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Preview;

/// <summary>預覽握把的一次拖曳事件。</summary>
internal sealed class PreviewResizeDragEventArgs : EventArgs
{
    public PreviewResizeDragEventArgs(
        PreviewResizeCorner corner,
        double horizontalChange,
        double verticalChange,
        bool canceled = false)
    {
        Corner = corner;
        HorizontalChange = horizontalChange;
        VerticalChange = verticalChange;
        Canceled = canceled;
    }

    public PreviewResizeCorner Corner { get; }

    /// <summary>相對按下瞬間的總位移，不是上一幀到這一幀的增量。</summary>
    public double HorizontalChange { get; }

    public double VerticalChange { get; }

    public bool Canceled { get; }
}

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
internal sealed class SqlStructurePreviewControl : UserControl, IDisposable
{
    /// <summary>
    /// 角落握把的邊長。
    /// </summary>
    /// <remarks>
    /// 不只是外觀尺寸：呼叫端拿它當「這一軸算不算被拖過」的門檻，所以寫死在兩邊
    /// 會出現「握把改大了、門檻沒跟著改」這種看不出關聯的失準。
    /// </remarks>
    public const double GripSize = 16;

    private sealed class ColumnRow
    {
        public ColumnRow(SqlColumnInfo column)
        {
            Ordinal = column.Ordinal;
            Name = column.Name;
            DataType = column.DataType;
            FlagList = PreviewChrome.BuildFlags(column);
            Flags = string.Join(" ", FlagList);
            Computed = column.IsComputed ? column.ComputedDefinition ?? "COMPUTED" : string.Empty;
            Default = column.DefaultDefinition ?? string.Empty;
        }

        public int Ordinal { get; }

        public string Name { get; }

        public string DataType { get; }

        /// <summary>畫成一列膠囊徽章的旗標。</summary>
        public IReadOnlyList<string> FlagList { get; }

        /// <summary>複製時用的純文字版本；徽章欄不是文字欄，複製要有東西可以讀。</summary>
        public string Flags { get; }

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

    /// <summary>
    /// 標題列動作按鈕的字級。
    /// </summary>
    /// <remarks>
    /// 刻意不跟著「預覽視窗的字級」那個設定走：那個設定調的是內容的可讀性，
    /// 標題列是視窗外框的一部分，跟著放大只會把內容擠掉。
    /// </remarks>
    private static readonly SqlAssistChrome.Metrics TitleBarMetrics = new(12);

    private readonly CrispImage _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _summary;
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
    private readonly DataGridTemplateColumn _flags;
    private readonly Thumb _resizeLeft;
    private readonly Thumb _resizeRight;
    private readonly Border _root;

    /// <summary>目前套用的基準字級；相同就不重建樣式。</summary>
    private double _fontSize;

    /// <summary>按下握把當下的游標位置與尺寸；拖曳中的每一步都以此為基準重算。</summary>
    private Point? _dragOrigin;

    private Matrix _dragTransformToDevice = Matrix.Identity;

    private double _fallbackHorizontalChange;

    private double _fallbackVerticalChange;

    private double _lastHorizontalChange;

    private double _lastVerticalChange;

    private PreviewResizeCorner _activeResizeCorner;

    private int _openContextMenuCount;

    /// <summary>已經填過內容的分頁；換了物件就整批清掉。</summary>
    private readonly HashSet<TabItem> _populated = new();

    private readonly List<ContextMenu> _contextMenus = new();

    /// <summary>目前顯示的結構；分頁按需填內容時要回頭讀它。</summary>
    private SqlObjectStructure? _structure;

    /// <summary>指令碼只組一次；複製與顯示都用同一份。</summary>
    private string? _scriptText;

    private readonly IWpfTextView _view;
    private SqlScriptTheme? _scriptTheme;

    public SqlStructurePreviewControl(IWpfTextView view)
    {
        _view = view;
        VsThemeBrushes.Apply(this);
        _icon = PreviewChrome.CreateObjectIcon();

        _title = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            TextTrimming = TextTrimming.CharacterEllipsis
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);

        // 摘要從底部搬到標題底下：物件的欄位數與主索引鍵是「這是什麼」的一部分，
        // 該跟名字待在一起。底部那一條留給操作之後的回饋，平常是空的。
        _summary = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.DimForeground);

        _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
        _status.Margin = new Thickness(24, 0, 24, 6);

        _columns = CreateGrid(
            ("#", nameof(ColumnRow.Ordinal)),
            ("欄位", nameof(ColumnRow.Name)),
            ("型別", nameof(ColumnRow.DataType)),
            ("計算欄位", nameof(ColumnRow.Computed)),
            ("預設值", nameof(ColumnRow.Default)));

        // NULL、PK、IDENTITY 三個文字欄收成一欄膠囊，插在型別後面。
        _flags = CreateFlagsColumn();
        _columns.Columns.Insert(3, _flags);

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

            // 浮動視窗拿不到鍵盤焦點，預設狀態下選取起來是看不見的。
            IsInactiveSelectionHighlightEnabled = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 0, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ContextMenu = CreateScriptMenu()
        }.WithTheme(RichTextBox.BackgroundProperty, ThemeBrush.ListBackground)
            .WithTheme(RichTextBox.ForegroundProperty, ThemeBrush.ListForeground)
            .WithTheme(RichTextBox.SelectionBrushProperty, ThemeBrush.RowSelected);

        _columnsTab = new TabItem { Header = "欄位", Content = _columns };
        _indexesTab = new TabItem { Header = "索引", Content = _indexes };
        _foreignKeysTab = new TabItem { Header = "外來鍵", Content = _foreignKeys };
        _parametersTab = new TabItem { Header = "參數", Content = _parameters };
        _scriptTab = new TabItem { Header = "指令碼", Content = _script };

        var segment = SqlAssistChrome.CreateTabItemTemplate();
        _columnsTab.Template = segment;
        _indexesTab.Template = segment;
        _foreignKeysTab.Template = segment;
        _parametersTab.Template = segment;
        _scriptTab.Template = segment;

        _tabs = new TabControl
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = SqlAssistChrome.InterfaceFont,
            Template = SqlAssistChrome.CreateTabControlTemplate()
        }.WithTheme(TabControl.BackgroundProperty, ThemeBrush.ListBackground);
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

        // 名字與摘要疊成兩行：第一行回答「這是誰」，第二行回答「它有多大」。
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(_title);
        caption.Children.Add(_summary);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(14, 12, 10, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(_icon, Dock.Left);
        header.Children.Add(buttons);
        header.Children.Add(_icon);
        header.Children.Add(caption);

        _resizeLeft = CreateResizeThumb(PreviewResizeCorner.BottomLeft);
        _resizeLeft.HorizontalAlignment = HorizontalAlignment.Left;
        _resizeLeft.Cursor = Cursors.SizeNESW;
        _resizeLeft.RenderTransform = new ScaleTransform(-1, 1, 8, 8);

        _resizeRight = CreateResizeThumb(PreviewResizeCorner.BottomRight);
        _resizeRight.HorizontalAlignment = HorizontalAlignment.Right;
        _resizeRight.Cursor = Cursors.SizeNWSE;

        var footer = new Grid();
        footer.Children.Add(_status);

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

        // 握把放在整個內容的 overlay，落在上方時才能移到上緣而不受 footer 限制。
        var overlay = new Grid();
        overlay.Children.Add(layout);
        overlay.Children.Add(_resizeLeft);
        overlay.Children.Add(_resizeRight);

        _root = new Border
        {
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = overlay
        }.WithTheme(Border.BackgroundProperty, ThemeBrush.WindowBackground)
            .WithTheme(Border.BorderBrushProperty, ThemeBrush.Border);

        // 原生圖示依實際底色轉換，避免深色與高對比主題出現不相容的光暈。
        _root.SetBinding(ImageThemingUtilities.ImageBackgroundColorProperty, new Binding(nameof(Border.Background))
        {
            Source = _root,
            Converter = new BrushToColorConverter()
        });

        // 版面計算的模式交給排版而不是像素對齊：字距在小字級下才不會忽寬忽窄。
        TextOptions.SetTextFormattingMode(_root, TextFormattingMode.Ideal);

        // 整組字級都從設定推導，這裡沒有任何寫死的數字可以跟設定不同步。
        ApplyFontSize(SqlAssistSettingsStore.Current.PreviewFontSize);

        Content = _root;

        // 顯示時不主動搶焦點：使用者還在打字，游標必須留在編輯器裡。
        // 點進來才接受焦點，那時才需要能夠拉選文字。
        Focusable = false;
    }

    public event EventHandler<PreviewResizeDragEventArgs>? ResizeStarted;

    public event EventHandler<PreviewResizeDragEventArgs>? ResizeDelta;

    public event EventHandler<PreviewResizeDragEventArgs>? ResizeCompleted;

    public event EventHandler? SizeResetRequested;

    /// <summary>右鍵選單是另一個 Popup，Agent 要把它一起算進聚合焦點。</summary>
    public event EventHandler? InteractionFocusGained;

    public event EventHandler? InteractionFocusLost;

    /// <summary>使用者在預覽裡按下 Esc。</summary>
    public event EventHandler? CloseRequested;

    public bool HasOpenContextMenu => _openContextMenuCount > 0;

    /// <summary>只套用這一輪真正顯示的尺寸；不代表使用者的持久偏好。</summary>
    public void SetEffectiveSize(double width, double height)
    {
        _root.Width = width;
        _root.Height = height;
    }

    /// <summary>上方落點改用上緣握把，固定 Bottom 往上增高；其餘情況使用下緣。</summary>
    public void SetResizeEdge(bool onTop)
    {
        _resizeLeft.Tag = onTop ? PreviewResizeCorner.TopLeft : PreviewResizeCorner.BottomLeft;
        _resizeRight.Tag = onTop ? PreviewResizeCorner.TopRight : PreviewResizeCorner.BottomRight;
        _resizeLeft.VerticalAlignment = onTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _resizeRight.VerticalAlignment = onTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _resizeLeft.Cursor = onTop ? Cursors.SizeNWSE : Cursors.SizeNESW;
        _resizeRight.Cursor = onTop ? Cursors.SizeNESW : Cursors.SizeNWSE;
        _resizeLeft.RenderTransform = new ScaleTransform(-1, onTop ? -1 : 1, 8, 8);
        _resizeRight.RenderTransform = onTop
            ? new ScaleTransform(1, -1, 8, 8)
            : Transform.Identity;
        _status.Margin = onTop ? new Thickness(14, 0, 14, 6) : new Thickness(24, 0, 24, 6);

        AutomationProperties.SetName(_resizeLeft, onTop ? "左上角調整大小" : "左下角調整大小");
        AutomationProperties.SetName(_resizeRight, onTop ? "右上角調整大小" : "右下角調整大小");
    }

    public void CloseTransientPopups()
    {
        foreach (var menu in _contextMenus)
        {
            menu.IsOpen = false;
        }

        _openContextMenuCount = 0;
    }

    /// <summary>換一個物件：標題先出來，內容等資料到齊。</summary>
    public void SetTarget(SqlObjectInfo objectInfo)
    {
        _structure = null;
        _scriptText = null;
        _populated.Clear();
        SetTitle(objectInfo);
        _summary.Text = objectInfo.Kind.ToDisplayName() + "　載入中…";
        _status.Text = string.Empty;
        ClearTabs();
    }

    /// <summary>
    /// 標題永遠寫在填內容的同一條路上。
    /// </summary>
    /// <remarks>
    /// 只在 <see cref="SetTarget"/> 裡寫標題是不夠的：那條路只有快取沒命中時才走。
    /// 命中第四層時呼叫端會直接 <see cref="Populate(SqlObjectStructure)"/>，
    /// 標題就會停在上一個物件上——畫面出現「標題是同義字、內容是資料表」。
    ///
    /// 種類文字併入淡色摘要，圖示只輔助辨識，讓名稱維持標題的視覺重點。
    /// </remarks>
    private void SetTitle(SqlObjectInfo objectInfo)
    {
        _icon.Moniker = SqlIcons.GetMoniker(objectInfo.Kind);
        _icon.ToolTip = objectInfo.Kind.ToDisplayName();
        AutomationProperties.SetName(_icon, SqlIcons.GetImageElement(objectInfo.Kind).AutomationName);
        _icon.Visibility = Visibility.Visible;

        _title.Inlines.Clear();
        _title.Inlines.Add(new Run(SqlIdentifier.Quote(objectInfo.SchemaName) + ".")
                .WithTheme(Run.ForegroundProperty, ThemeBrush.DimForeground));
        _title.Inlines.Add(new Run(SqlIdentifier.Quote(objectInfo.Name))
        {
            FontWeight = FontWeights.SemiBold
        });
    }

    /// <summary>顯示一段訊息取代內容，例如沒有連線或這一項沒有結構。</summary>
    public void ShowMessage(string title, string message)
    {
        _structure = null;
        _scriptText = null;
        _populated.Clear();

        // 沒有物件語意的訊息不顯示圖示，避免誤認為未知種類的物件。
        _icon.Visibility = Visibility.Collapsed;
        _title.Inlines.Clear();
        _title.Inlines.Add(new Run(title));
        _summary.Text = message;
        _status.Text = string.Empty;
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
        SetTitle(structure.Object);

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
        _summary.Text = BuildSummary(structure, partial);
        _status.Text = string.Empty;
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
                _scriptTheme ??= new SqlScriptTheme(_view, _script);
                _scriptTheme.EnsureCurrent();
                _script.Document = SqlScriptDocument.Build(GetScript(), _scriptTheme.Resources);
            }
        }
        catch (Exception exception)
        {
            // 不走 SqlAssistPlatformGuard：使用者是自己切到這個分頁的，
            // 一片空白而沒有任何說明會被當成資料真的是空的。
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

    /// <summary>
    /// 讀出某一格要複製的文字。
    /// </summary>
    /// <remarks>
    /// 徽章欄不是文字欄，沒有繫結路徑可以讀，於是退回
    /// <see cref="DataGridColumn.SortMemberPath"/>——那裡指向旗標的純文字版本。
    /// 少了這一段，複製欄位表就會多出一個永遠是空的欄。
    /// </remarks>
    private static string GetCellText(DataGridColumn column, object row)
    {
        var path = column switch
        {
            DataGridTextColumn { Binding: Binding { Path.Path: { Length: > 0 } bound } } => bound,
            _ => column.SortMemberPath
        };

        if (string.IsNullOrEmpty(path))
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
        VsThemeBrushes.Apply(menu);
        var copy = new MenuItem { Header = "複製選取內容" };
        copy.Click += (_, _) => CopySelection();
        var copyAll = new MenuItem { Header = "複製完整指令碼" };
        copyAll.Click += (_, _) => CopyAll();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        TrackContextMenu(menu);
        return menu;
    }

    private ContextMenu CreateGridMenu()
    {
        var menu = new ContextMenu();
        VsThemeBrushes.Apply(menu);
        var copy = new MenuItem { Header = "複製選取的儲存格" };
        copy.Click += (_, _) => CopySelection();
        var copyAll = new MenuItem { Header = "複製整個表格" };
        copyAll.Click += (_, _) => CopyGridAll();
        var copyScript = new MenuItem { Header = "複製完整指令碼" };
        copyScript.Click += (_, _) => CopyAll();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        menu.Items.Add(copyScript);
        TrackContextMenu(menu);
        return menu;
    }

    private void TrackContextMenu(ContextMenu menu)
    {
        _contextMenus.Add(menu);
        menu.Opened += (_, _) =>
        {
            _openContextMenuCount++;
            InteractionFocusGained?.Invoke(this, EventArgs.Empty);
        };
        menu.Closed += (_, _) =>
        {
            _openContextMenuCount = Math.Max(0, _openContextMenuCount - 1);
            InteractionFocusLost?.Invoke(this, EventArgs.Empty);
        };
    }

    private void OnResizeDragStarted(object sender, DragStartedEventArgs eventArgs)
    {
        _activeResizeCorner = sender is Thumb { Tag: PreviewResizeCorner corner }
            ? corner
            : PreviewResizeCorner.BottomRight;
        _dragOrigin = NativeCursor.TryGetPosition();
        _dragTransformToDevice = NativeScreen.GetTransformToDevice(this);
        _fallbackHorizontalChange = 0;
        _fallbackVerticalChange = 0;
        _lastHorizontalChange = 0;
        _lastVerticalChange = 0;
        ResizeStarted?.Invoke(
            this,
            new PreviewResizeDragEventArgs(_activeResizeCorner, 0, 0));
    }

    /// <summary>
    /// 依游標相對於按下瞬間的位移重算尺寸。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <see cref="DragDeltaEventArgs"/> 帶來的位移量：那是相對於握把的父代
    /// 算出來的，而浮動視窗在調整大小的過程中會被平台重新定位，父代自己在動，
    /// 於是視窗的移動會被誤算成滑鼠的移動而形成回授，畫面就開始亂跳。
    /// 以絕對座標重算，尺寸是「起始尺寸 ＋ 游標位移」這個純函式，不受視窗移動影響。
    ///
    /// DPI 轉換矩陣在按下時一併凍結；拖過不同縮放比例的螢幕時，不會讓已走過的
    /// 整段距離突然換一個倍率。
    /// </remarks>
    private void OnResizeDragDelta(object sender, DragDeltaEventArgs eventArgs)
    {
        if (_dragOrigin is not { } origin || NativeCursor.TryGetPosition() is not { } current)
        {
            // 平台給的是逐幀增量；先累積成相對起點的總量，才能維持路徑無關。
            _fallbackHorizontalChange += eventArgs.HorizontalChange;
            _fallbackVerticalChange += eventArgs.VerticalChange;
            var deviceChange = _dragTransformToDevice.Transform(
                new Vector(_fallbackHorizontalChange, _fallbackVerticalChange));
            _lastHorizontalChange = deviceChange.X;
            _lastVerticalChange = deviceChange.Y;
        }
        else
        {
            // 原生游標本來就是實體像素；外層定位引擎也使用同一座標系。
            var moved = current - origin;
            _lastHorizontalChange = moved.X;
            _lastVerticalChange = moved.Y;
        }

        ResizeDelta?.Invoke(
            this,
            new PreviewResizeDragEventArgs(
                _activeResizeCorner,
                _lastHorizontalChange,
                _lastVerticalChange));
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs eventArgs)
    {
        _dragOrigin = null;
        ResizeCompleted?.Invoke(
            this,
            new PreviewResizeDragEventArgs(
                _activeResizeCorner,
                _lastHorizontalChange,
                _lastVerticalChange,
                eventArgs.Canceled));
    }

    private void OnResizeDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        _status.Text = "已重設目前擺放方式的預覽尺寸。";
        SizeResetRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>視窗剛掛上去時淡入一次；換選取時不重播，那會變成閃爍。</summary>
    public void PlayAppear() => PreviewChrome.PlayAppear(_root);

    /// <summary>目前分頁有沒有選取的內容；決定 Ctrl+C 該不該由預覽接手。</summary>
    public bool HasSelection()
    {
        if (_tabs.SelectedItem is not TabItem tab)
        {
            return false;
        }

        if (ReferenceEquals(tab, _scriptTab))
        {
            return !string.IsNullOrEmpty(_script.Selection?.Text);
        }

        return tab.Content is DataGrid grid && grid.SelectedCells.Count > 0;
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
        var builder = new StringBuilder(structure.Object.Kind.ToDisplayName());

        if (structure.Columns.Count > 0)
        {
            Separate(builder);
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

        return builder.ToString();
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
        var button = SqlAssistChrome.CreateButton(text, TitleBarMetrics);
        button.Margin = new Thickness(2, 0, 0, 0);
        button.Padding = new Thickness(10, 3, 10, 4);
        button.ToolTip = tooltip;

        // 標題列的動作比內容次要一階，用淡一級的前景色。
        button.SetResourceReference(Control.ForegroundProperty, ThemeBrush.DimForeground);

        // 按鈕不吃焦點：按一下複製之後，焦點該留在原本選取的地方。
        button.Focusable = false;

        button.Click += (_, _) => click();
        return button;
    }

    private Thumb CreateResizeThumb(PreviewResizeCorner corner)
    {
        var thumb = new Thumb
        {
            Width = GripSize,
            Height = GripSize,
            Tag = corner,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Bottom,
            Template = CreateResizeGripTemplate(),
            ToolTip = "拖曳這一側調整寬高；雙擊重設目前擺放方式的尺寸"
        };
        AutomationProperties.SetName(
            thumb,
            corner == PreviewResizeCorner.BottomLeft ? "左下角調整大小" : "右下角調整大小");
        thumb.DragStarted += OnResizeDragStarted;
        thumb.DragDelta += OnResizeDragDelta;
        thumb.DragCompleted += OnResizeDragCompleted;
        thumb.MouseDoubleClick += OnResizeDoubleClick;
        return thumb;
    }

    /// <summary>
    /// 左右兩側、上下落點共用的縮放握把。
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
        lines.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, ThemeBrush.DimForeground);
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
        var grid = SqlAssistChrome.CreateDataGrid(SqlAssistChrome.DefaultMetrics);

        // 外觀之外的都是這個視窗自己的行為：以儲存格為選取單位、允許橫向捲動，
        // 以及一份自己的內容選單。
        grid.IsReadOnly = true;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.ContextMenu = CreateGridMenu();

        var cellText = SqlAssistChrome.CreateCellTextStyle();

        foreach (var (header, path) in columns)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                ElementStyle = cellText,
                Width = DataGridLength.Auto
            });
        }

        return grid;
    }

    /// <summary>
    /// 把旗標畫成一列膠囊的欄。
    /// </summary>
    /// <remarks>
    /// <see cref="SortMemberPath"/> 不是為了排序才設的——這一欄不是文字欄，
    /// 複製時讀不到繫結路徑。複製的程式碼會退回這個路徑，因此它必須指向
    /// 旗標的純文字版本。
    /// </remarks>
    private static DataGridTemplateColumn CreateFlagsColumn()
    {
        return new DataGridTemplateColumn
        {
            Header = "旗標",
            SortMemberPath = nameof(ColumnRow.Flags),
            Width = DataGridLength.Auto
        };
    }

    /// <summary>
    /// 套用基準字級。
    /// </summary>
    /// <remarks>
    /// 每次顯示都呼叫一次，設定改完不必重開查詢視窗就會生效。相同的值直接返回，
    /// 因為重建樣式會讓資料格重新量一次所有欄寬——那是換選取時最不該付的成本。
    ///
    /// 資料格的字級靠繼承傳給儲存格，但欄位標題與徽章的字級是寫在樣式與範本裡的，
    /// 那兩樣只能整個換掉。指令碼分頁不動，它跟的是編輯器的字型與字級。
    /// </remarks>
    public void ApplyFontSize(double baseSize)
    {
        if (Math.Abs(_fontSize - baseSize) < 0.01)
        {
            return;
        }

        _fontSize = baseSize;
        var metrics = new SqlAssistChrome.Metrics(baseSize);

        _title.FontSize = metrics.Title;
        _summary.FontSize = metrics.Caption;
        _status.FontSize = metrics.Caption;
        _tabs.FontSize = metrics.Body;

        var headerStyle = SqlAssistChrome.CreateColumnHeaderStyle(metrics);

        foreach (var grid in new[] { _columns, _indexes, _foreignKeys, _parameters })
        {
            grid.FontSize = metrics.Body;
            grid.RowHeight = metrics.RowHeight;
            grid.ColumnHeaderStyle = headerStyle;
        }

        _flags.CellTemplate = PreviewChrome.CreateFlagsCellTemplate(
            nameof(ColumnRow.FlagList),
            metrics);
    }

    public void Dispose()
    {
        _scriptTheme?.Dispose();
        _scriptTheme = null;
    }
}
