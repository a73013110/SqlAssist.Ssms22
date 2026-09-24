using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Matching;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Preview;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Editor;
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
/// 著色改由共用的 <see cref="SqlReadOnlyViewer"/> 自己排，顏色仍向編輯器借。
///
/// 複製一律走明確的處理常式與標題列按鈕，不依賴
/// <see cref="ApplicationCommands.Copy"/> 的繞送：浮動視窗裡的鍵盤焦點
/// 未必落在預期的元素上，命令繞送不到就會變成「選得起來但複製不了」。
/// </remarks>
internal sealed class SqlStructurePreviewControl : UserControl, IShellKeyTarget, IDisposable
{
    /// <summary>
    /// 角落握把的邊長。
    /// </summary>
    /// <remarks>
    /// 不只是外觀尺寸：呼叫端拿它當「這一軸算不算被拖過」的門檻，所以寫死在兩邊
    /// 會出現「握把改大了、門檻沒跟著改」這種看不出關聯的失準。
    /// </remarks>
    public const double GripSize = 16;

    /// <summary>
    /// 內建對照表的一列。
    /// </summary>
    /// <remarks>
    /// 四個具名屬性而不是索引子：資料格的複製與空欄收合都靠繫結路徑反射屬性，
    /// <c>[0]</c> 這種路徑在那條路上讀不出來，症狀是整張表複製出來一片空白。
    /// </remarks>
    private sealed class ReferenceRow
    {
        public ReferenceRow(IReadOnlyList<string> cells)
        {
            Cell1 = Cell(cells, 0);
            Cell2 = Cell(cells, 1);
            Cell3 = Cell(cells, 2);
            Cell4 = Cell(cells, 3);
        }

        public string Cell1 { get; }

        public string Cell2 { get; }

        public string Cell3 { get; }

        public string Cell4 { get; }

        private static string Cell(IReadOnlyList<string> cells, int index) =>
            index < cells.Count ? cells[index] : string.Empty;
    }

    /// <summary>
    /// 一張內建對照表的分頁。
    /// </summary>
    /// <remarks>
    /// 重複使用而不是每次顯示都建一組新的：建立資料格會連帶建立一份內容選單，
    /// 而那份選單會被登記到 <c>_contextMenus</c> 裡跟著視窗一輩子。
    /// 欄位標題與可見性每次重設，欄數上限是四。
    /// </remarks>
    private sealed class ReferenceTab
    {
        public ReferenceTab(TabItem item, SqlTabHeader header, DataGrid grid)
        {
            Item = item;
            Header = header;
            Grid = grid;
        }

        public TabItem Item { get; }

        public SqlTabHeader Header { get; }

        public DataGrid Grid { get; }
    }

    private sealed class ColumnRow
    {
        public ColumnRow(SqlColumnInfo column)
        {
            Ordinal = column.Ordinal;
            Name = column.Name;
            DataType = column.DataType;
            FlagList = PreviewChrome.BuildFlags(column);
            Flags = string.Join(" ", FlagList);
            Description = SqlDescriptionText.Collapse(column.Description) ?? string.Empty;
            Computed = DescribeComputed(column);
            Default = column.DefaultDefinition ?? string.Empty;
        }

        public int Ordinal { get; }

        public string Name { get; }

        public string DataType { get; }

        /// <summary>畫成一列膠囊徽章的旗標。</summary>
        public IReadOnlyList<string> FlagList { get; }

        /// <summary>複製時用的純文字版本；徽章欄不是文字欄，複製要有東西可以讀。</summary>
        public string Flags { get; }

        /// <summary>資料行的 <c>MS_Description</c>；收斂成單行，全文留在 Tooltip 裡。</summary>
        public string Description { get; }

        public string Computed { get; }

        public string Default { get; }

        /// <remarks>
        /// <c>PERSISTED</c> 併進運算式那一欄，不另給徽章：T-SQL 自己就把它寫在
        /// 運算式後面（<c>AS (…) PERSISTED</c>），而它只對計算資料行有意義——
        /// 開一整欄給一個九成九是空的旗標，是那一欄自己不划算。
        /// </remarks>
        private static string DescribeComputed(SqlColumnInfo column)
        {
            if (!column.IsComputed)
            {
                return string.Empty;
            }

            var expression = column.ComputedDefinition ?? "COMPUTED";

            return column.Script.IsPersisted ? expression + " PERSISTED" : expression;
        }
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
            Options = DescribeOptions(index);
            Location = index.DataSpace?.ToString() ?? string.Empty;
        }

        public string Name { get; }

        public string Kind { get; }

        public string KeyColumns { get; }

        public string IncludedColumns { get; }

        public string Filter { get; }

        /// <summary>停用狀態，以及與預設值不同的那幾個 <c>WITH</c> 選項。</summary>
        public string Options { get; }

        /// <summary>檔案群組，或分割配置加上分割資料行。</summary>
        public string Location { get; }

        /// <remarks>
        /// 停用與選項擺同一欄：兩者都在回答「這個索引跟預設的不一樣在哪裡」，
        /// 分成兩欄的話那兩欄有九成的列都是空的。哪些算預設值由
        /// <see cref="SqlIndexOptions.DescribeNonDefaults"/> 回答，這裡不再判斷一次。
        ///
        /// 停用寫在最前面：它不是一個選項，是「這個索引現在沒有資料」。
        /// </remarks>
        private static string DescribeOptions(SqlIndexInfo index)
        {
            var options = index.Options.DescribeNonDefaults();

            if (!index.Options.IsDisabled)
            {
                return string.Join(", ", options);
            }

            return options.Count == 0
                ? DisabledText
                : DisabledText + "　" + string.Join(", ", options);
        }
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
            Ordinal = parameter.Ordinal;
            Name = parameter.Name;
            DataType = parameter.DataType;
            Direction = parameter.IsOutput ? "OUTPUT" : string.Empty;
        }

        /// <summary>參數的順序；照位置傳引數時要的正是這個號碼。</summary>
        public int Ordinal { get; }

        public string Name { get; }

        public string DataType { get; }

        public string Direction { get; }
    }

    private sealed class CheckRow
    {
        public CheckRow(SqlCheckConstraint constraint)
        {
            Name = constraint.Name;
            Column = constraint.ColumnName ?? string.Empty;
            Definition = constraint.Definition;
            State = DescribeState(constraint);
        }

        public string Name { get; }

        /// <summary>寫在單一資料行上的條件約束；寫在資料表層級時是空的。</summary>
        public string Column { get; }

        public string Definition { get; }

        public string State { get; }

        /// <remarks>
        /// 停用一定要標。看的人多半正在問「為什麼這筆資料進得來」，
        /// 而一個停用的 <c>CHECK</c> 在清單上與啟用的長得一模一樣。
        /// 系統命名也一起標：那種名字每建一次就換一個，照著它寫指令碼會讓
        /// 同一張表在兩個資料庫裡對不起來。
        /// </remarks>
        private static string DescribeState(SqlCheckConstraint constraint)
        {
            var parts = new List<string>(3);

            if (constraint.IsDisabled)
            {
                parts.Add(DisabledText);
            }

            if (constraint.IsNotForReplication)
            {
                parts.Add("NOT FOR REPLICATION");
            }

            if (constraint.IsSystemNamed)
            {
                parts.Add("系統命名");
            }

            return string.Join("　", parts);
        }
    }

    private sealed class TriggerRow
    {
        public TriggerRow(SqlTriggerInfo trigger)
        {
            Name = trigger.Name;
            State = DescribeState(trigger);
        }

        public string Name { get; }

        public string State { get; }

        /// <remarks>
        /// 取不到定義要寫出來，不能只是留白：那一句是「這個觸發程序仍然會執行，
        /// 只是這裡看不到它做什麼」，而空白會被讀成「它沒做什麼」。
        /// </remarks>
        private static string DescribeState(SqlTriggerInfo trigger)
        {
            var parts = new List<string>(2);

            if (trigger.IsDisabled)
            {
                parts.Add(DisabledText);
            }

            if (!trigger.CanScript)
            {
                parts.Add("無法取得定義（已加密或權限不足）");
            }

            return string.Join("　", parts);
        }
    }

    /// <summary>索引、條件約束與觸發程序共用的停用字樣。</summary>
    private const string DisabledText = "已停用";

    /// <summary>
    /// 一個資料格分頁的完整宣告。
    /// </summary>
    /// <remarks>
    /// 可見性、內容與「這一頁要不要等第四層」寫在同一個地方。分散成三段
    /// if-else 的症狀是新增一個分頁時漏掉其中一段——漏可見性是空分頁留在畫面上，
    /// 漏填入是切過去一片空白，而兩者都不會編譯失敗。
    ///
    /// 數量與資料列分成兩個委派：可見性與分頁上的數字在每一次換物件時都要問，
    /// 而資料列只有使用者真的切過去（或搜尋要數命中）才建。
    /// </remarks>
    private sealed class GridTab
    {
        public GridTab(
            string header,
            DataGrid grid,
            bool requiresStructure,
            Func<SqlObjectStructure, int> count,
            Func<SqlObjectStructure, System.Collections.IEnumerable> rows)
        {
            Header = new SqlTabHeader(header);
            Item = new TabItem { Header = Header, Content = grid };
            Grid = grid;
            RequiresStructure = requiresStructure;
            Count = count;
            Rows = rows;
        }

        public TabItem Item { get; }

        public SqlTabHeader Header { get; }

        public DataGrid Grid { get; }

        /// <summary>第四層還沒到齊時，空清單不是答案而是「還沒問到」。</summary>
        public bool RequiresStructure { get; }

        public Func<SqlObjectStructure, int> Count { get; }

        public Func<SqlObjectStructure, System.Collections.IEnumerable> Rows { get; }
    }

    /// <summary>
    /// 資料格的一欄。
    /// </summary>
    /// <remarks>
    /// 兩件事只有欄自己知道，因此寫在宣告裡而不是散在建立資料格的迴圈裡：
    ///
    /// 自由文字欄（說明、運算式、篩選條件、條件約束定義）不能讓一段兩百字的說明把它
    /// 後面的每一欄都推出視窗外。每張表最長的那一欄吃剩餘寬度（<see cref="Fill"/>），
    /// 其餘自由文字欄設寬度上限；只設上限的那一版在寬視窗裡照樣把說明截斷，而右邊
    /// 明明還空著一大片。名稱與型別不設上限——那幾欄本來就短。
    ///
    /// 有些欄整張表都是空的（沒有計算資料行、沒有掛說明、沒有篩選索引）。那一欄
    /// 仍佔著標題與內距而一個字都沒有，所以整欄收掉。
    /// </remarks>
    private readonly struct GridColumn
    {
        /// <summary>自由文字欄的寬度上限，以預設預覽寬度（620）估的。</summary>
        public const double TextWidth = 260;

        /// <summary>吃剩餘寬度的那一欄再窄也保留的寬度；再窄就一個詞都讀不完。</summary>
        public const double FillMinimumWidth = 160;

        public GridColumn(string header, string path, double maximumWidth = 0, bool optional = false, bool fill = false)
        {
            Header = header;
            Path = path;
            MaximumWidth = maximumWidth;
            Optional = optional;
            Fill = fill;
        }

        public string Header { get; }

        public string Path { get; }

        /// <summary>0 代表不設上限。</summary>
        public double MaximumWidth { get; }

        /// <summary>這一次的資料裡整欄都是空的時候收掉。</summary>
        public bool Optional { get; }

        /// <summary>吃剩餘寬度；一張表只給最長的那一欄。</summary>
        public bool Fill { get; }

        /// <summary>會長到讀不完的那幾欄；換行開關只作用在它們身上。</summary>
        public bool IsFreeText => Fill || MaximumWidth > 0;

        public static implicit operator GridColumn((string Header, string Path) column) =>
            new(column.Header, column.Path);
    }

    /// <summary>
    /// 分頁列右側工具的字級。
    /// </summary>
    /// <remarks>
    /// 刻意不跟著「預覽視窗的字級」那個設定走：那個設定調的是內容的可讀性，
    /// 工具是視窗外框的一部分，跟著放大只會把分頁擠掉。
    /// </remarks>
    private static readonly SqlAssistChrome.Metrics ToolMetrics = new(12);

    /// <summary>搜尋框的寬度；夠打一個欄名，又不把分頁擠出這一列。</summary>
    private const double SearchWidth = 168;

    private readonly TextBlock _title;
    private readonly CrispImage _kindIcon;
    private readonly SqlPill _kindPill;
    private readonly SqlPill _keyPill;
    private readonly SqlPill _pendingPill;
    private readonly SqlPill _failurePill;
    private readonly TextBlock _signature;
    private readonly TextBlock _description;
    private readonly TextBlock _status;
    private readonly TabControl _tabs;

    /// <summary>資料格分頁，順序就是畫面上的順序。</summary>
    private readonly GridTab[] _gridTabs;

    private readonly TabItem _scriptTab;
    private readonly SqlTabHeader _scriptHeader;
    private readonly SqlReadOnlyViewer _script;
    private readonly SqlMatchNavigation _scriptMatches;
    private readonly StackPanel _scriptNavigation;
    private readonly TextBox _search;
    private readonly Border _searchBar;
    private readonly Button _clearSearch;
    private readonly ToggleButton _wrap;
    private readonly DispatcherTimer _searchDelay;
    private readonly DataGridTemplateColumn _flags;
    private readonly Thumb _resizeLeft;
    private readonly Thumb _resizeRight;
    private readonly Border _root;

    /// <summary>目前套用的字級；相同就不重建樣式。</summary>
    private SqlAssistChrome.Metrics _metrics;

    private double _fontSize;

    /// <summary>自由文字欄的兩種儲存格樣式；換行開關換的是這一份，不重建資料格。</summary>
    private readonly Style _cellLine;

    private readonly Style _cellWrapped;

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

    /// <summary>已經建好的資料列；填入與搜尋數命中共用，換了物件就整批清掉。</summary>
    private readonly Dictionary<GridTab, System.Collections.IEnumerable> _rows = new();

    /// <summary>整欄都空就收掉的那些欄；每次填完資料重新判斷一次。</summary>
    private readonly HashSet<DataGridColumn> _optionalColumns = new();

    /// <summary>會長到讀不完的那幾欄；換行開關只換它們的樣板。</summary>
    private readonly List<DataGridTemplateColumn> _freeTextColumns = new();

    private readonly List<ContextMenu> _contextMenus = new();

    /// <summary>內建對照表的分頁，依需要長出來之後就留著重複使用。</summary>
    private readonly List<ReferenceTab> _referenceTabs = new();

    /// <summary>目前畫的是內建說明而不是資料庫物件。</summary>
    private SqlBuiltInDoc? _builtIn;

    /// <summary>目前顯示的結構；分頁按需填內容時要回頭讀它。</summary>
    private SqlObjectStructure? _structure;

    /// <summary>第四層還沒到齊；靠它的分頁與指令碼都還不能出現。</summary>
    private bool _partial;

    /// <summary>指令碼只組一次；複製與顯示都用同一份。</summary>
    private string? _scriptText;

    /// <summary>搜尋框裡目前生效的比對器；沒有搜尋字時 null。</summary>
    private TextMatcher? _matcher;

    private readonly IWpfTextView _view;

    public SqlStructurePreviewControl(IWpfTextView view)
    {
        _view = view;
        VsThemeBrushes.Apply(this);
        _metrics = SqlAssistChrome.DefaultMetrics;

        _title = new TextBlock
        {
            FontFamily = SqlAssistChrome.InterfaceFont,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        }.WithTheme(TextBlock.ForegroundProperty, ThemeBrush.ListForeground);
        _title.SetBinding(ToolTipProperty,
            new Binding(nameof(TextBlock.Text)) { Source = _title });

        // 抬頭只有一列：名稱後面接幾顆膠囊，順序與 SQL Search 清單列相同（名稱 → 種類 →
        // 次要標記）。種類、主索引鍵與載入狀態原本是名稱底下一整行淡色字，而規模數字
        // 已經寫在分頁標籤上——那一行留著只是把同一件事說兩次，還多佔一列。
        _kindIcon = PreviewChrome.CreateObjectIcon();
        _kindPill = new SqlPill(_kindIcon);

        // 主索引鍵是這個視窗唯一的強調色：它回答「這張表靠什麼認一筆」，而且只列欄名，
        // 排序方向要看的人到索引分頁去看——總覽裡的 ASC 是每一次都在的雜訊。
        _keyPill = new SqlPill(SqlIcon.PrimaryKey);

        _pendingPill = new SqlPill();

        // 第四層查詢失敗時底下每一頁都會說謊：沒有索引、沒有外來鍵、「沒有主索引鍵」
        // ——那全是空清單，不是答案。這一顆同時是使用者唯一看得到的線索。
        _failurePill = new SqlPill(SqlIcon.Warning)
        {
            Text = "索引與外來鍵讀取失敗",
            ToolTip = "原因見診斷紀錄檔（需開啟詳細記錄）"
        };

        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(SqlAssistChrome.Spacing.Group, 0, 0, 0)
        };

        foreach (var pill in new[] { _kindPill, _keyPill, _pendingPill, _failurePill })
        {
            pill.Margin = new Thickness(0, 0, SqlAssistChrome.Spacing.Tight, 0);
            pill.MaxWidth = 280;
            pill.Visibility = Visibility.Collapsed;
            pills.Children.Add(pill);
        }

        // 靠左而且膠囊先停：整列只量自己的寬度，放得下時膠囊緊跟在名稱後面；
        // 放不下時讓的是可以省略的名稱，膠囊不被擠出這一列。
        var titleRow = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(pills, Dock.Right);
        titleRow.Children.Add(pills);
        titleRow.Children.Add(_title);

        // 內建名稱的簽章自己一行：那是使用者開這個視窗時第一個要看的東西。
        _signature = SqlAssistChrome.CreateMetadataText(string.Empty, SqlAssistChrome.DefaultMetrics);
        _signature.Margin = new Thickness(0, 4, 0, 0);
        _signature.TextTrimming = TextTrimming.CharacterEllipsis;
        _signature.TextWrapping = TextWrapping.NoWrap;
        _signature.Visibility = Visibility.Collapsed;
        _signature.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _signature });

        // 資料表描述排在名稱底下：那是使用者自己寫的一句話，膠囊說不出來。
        // 沒有掛說明時整列收掉，不留一條空白撐高標題。
        _description = SqlAssistChrome.CreateMetadataText(string.Empty, SqlAssistChrome.DefaultMetrics);
        _description.Margin = new Thickness(0, 3, 0, 0);
        _description.TextTrimming = TextTrimming.CharacterEllipsis;
        _description.TextWrapping = TextWrapping.NoWrap;
        _description.Visibility = Visibility.Collapsed;
        _description.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _description });

        _status = SqlAssistChrome.CreateStatusText(SqlAssistChrome.DefaultMetrics);
        _status.Margin = new Thickness(24, 0, 24, 6);

        _cellLine = CreateCellStyle(wrap: false);
        _cellWrapped = CreateCellStyle(wrap: true);

        var columns = CreateGrid(
            ("#", nameof(ColumnRow.Ordinal)),
            ("欄位", nameof(ColumnRow.Name)),
            ("型別", nameof(ColumnRow.DataType)),
            new GridColumn("說明", nameof(ColumnRow.Description), optional: true, fill: true),
            new GridColumn("計算欄位", nameof(ColumnRow.Computed), GridColumn.TextWidth, optional: true),
            new GridColumn("預設值", nameof(ColumnRow.Default), GridColumn.TextWidth, optional: true));

        // NULL、PK、IDENTITY 三個文字欄收成一欄膠囊，插在型別後面。
        // 它與文字欄一樣可收：一張全部可為 NULL、又沒有主索引鍵的表一個徽章都沒有。
        _flags = CreateFlagsColumn();
        columns.Columns.Insert(3, _flags);
        _optionalColumns.Add(_flags);

        var indexes = CreateGrid(
            ("索引", nameof(IndexRow.Name)),
            ("種類", nameof(IndexRow.Kind)),
            ("索引鍵", nameof(IndexRow.KeyColumns)),
            new GridColumn("INCLUDE", nameof(IndexRow.IncludedColumns), optional: true),
            new GridColumn("篩選", nameof(IndexRow.Filter), GridColumn.TextWidth, optional: true),
            new GridColumn("選項", nameof(IndexRow.Options), optional: true),
            new GridColumn("位置", nameof(IndexRow.Location), optional: true));

        var foreignKeys = CreateGrid(
            ("外來鍵", nameof(ForeignKeyRow.Name)),
            new GridColumn("參考", nameof(ForeignKeyRow.Columns), fill: true),
            new GridColumn("動作", nameof(ForeignKeyRow.Actions), optional: true));

        var checks = CreateGrid(
            ("條件約束", nameof(CheckRow.Name)),
            new GridColumn("資料行", nameof(CheckRow.Column), optional: true),
            new GridColumn("定義", nameof(CheckRow.Definition), fill: true),
            new GridColumn("狀態", nameof(CheckRow.State), optional: true));

        var triggers = CreateGrid(
            ("觸發程序", nameof(TriggerRow.Name)),
            new GridColumn("狀態", nameof(TriggerRow.State), optional: true));

        var parameters = CreateGrid(
            ("#", nameof(ParameterRow.Ordinal)),
            ("參數", nameof(ParameterRow.Name)),
            ("型別", nameof(ParameterRow.DataType)),
            new GridColumn("方向", nameof(ParameterRow.Direction), optional: true));

        // 順序照使用者要問的次序：這張表有什麼（欄位）、它怎麼被找到（索引）、
        // 它跟誰有關（外來鍵）、什麼資料進得來（條件約束）、寫進去之後還會發生
        // 什麼（觸發程序）。參數只有模組有，與上面五個互斥。
        _gridTabs = new[]
        {
            new GridTab(
                "欄位",
                columns,
                requiresStructure: false,
                structure => structure.Columns.Count,
                structure => Map(structure.Columns, column => new ColumnRow(column))),
            new GridTab(
                "索引",
                indexes,
                requiresStructure: true,
                structure => structure.Indexes.Count,
                structure => Map(structure.Indexes, index => new IndexRow(index))),
            new GridTab(
                "外來鍵",
                foreignKeys,
                requiresStructure: true,
                structure => structure.ForeignKeys.Count,
                structure => Map(structure.ForeignKeys, key => new ForeignKeyRow(key))),
            new GridTab(
                "條件約束",
                checks,
                requiresStructure: true,
                structure => structure.CheckConstraints.Count,
                structure => Map(structure.CheckConstraints, check => new CheckRow(check))),
            new GridTab(
                "觸發程序",
                triggers,
                requiresStructure: true,
                structure => structure.Triggers.Count,
                structure => Map(structure.Triggers, trigger => new TriggerRow(trigger))),
            new GridTab(
                "參數",
                parameters,
                requiresStructure: false,
                structure => structure.Parameters.Count,
                structure => Map(structure.Parameters, parameter => new ParameterRow(parameter)))
        };

        // 指令碼走共用的唯讀 SQL 檢視：著色、原文複製與命中導覽與 SQL Search／Memory 的
        // 預覽是同一份，這裡不再自己養一個 RichTextBox。
        _script = new SqlReadOnlyViewer(embedded: true) { ReportError = message => _status.Text = message };
        TrackContextMenu(_script.Menu);
        _scriptMatches = new SqlMatchNavigation(_script);
        _scriptHeader = new SqlTabHeader("指令碼");
        _scriptTab = new TabItem { Header = _scriptHeader, Content = _script };

        var segment = SqlAssistChrome.CreateTabItemTemplate();

        foreach (var tab in _gridTabs)
        {
            tab.Item.Template = segment;
        }

        _scriptTab.Template = segment;

        _tabs = new TabControl
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = SqlAssistChrome.InterfaceFont,
            Template = SqlAssistChrome.CreateTabControlTemplate()
        }.WithTheme(TabControl.BackgroundProperty, ThemeBrush.ListBackground);

        foreach (var tab in _gridTabs)
        {
            _tabs.Items.Add(tab.Item);
        }

        _tabs.Items.Add(_scriptTab);
        _tabs.SelectionChanged += OnTabSelectionChanged;

        _search = SqlAssistChrome.CreateTextBox(ToolMetrics);
        _search.ToolTip = "在名稱、型別、說明與指令碼裡找；Enter／Shift+Enter 在指令碼裡跳到下一處／上一處";
        AutomationProperties.SetName(_search, "搜尋結構");
        _search.TextChanged += (_, _) => SqlAssistPlatformGuard.Run("輸入結構預覽搜尋", OnSearchTextChanged);
        _search.IsKeyboardFocusWithinChanged += (_, _) => SqlAssistPlatformGuard.Run("交接預覽搜尋的按鍵", OnSearchFocusChanged);

        _clearSearch = SqlAssistChrome.CreateIconButton(SqlIcon.Clear, "清除搜尋");
        _clearSearch.IsEnabled = false;
        _clearSearch.Focusable = false;
        _clearSearch.Click += (_, _) => SqlAssistPlatformGuard.Run("清除結構預覽搜尋", () => ResetSearch(keepFocus: true));

        _searchBar = SqlAssistChrome.CreateInputBar(SqlIcon.Search, _search, _clearSearch);
        _searchBar.Width = SearchWidth;

        _searchDelay = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = SqlAssistChrome.Debounce.Search };
        _searchDelay.Tick += (_, _) => SqlAssistPlatformGuard.Run("套用結構預覽搜尋", () =>
        {
            _searchDelay.Stop();
            ApplySearch();
        });

        // 導覽只屬於指令碼分頁：資料格的命中是用篩選看的，一列一列走沒有意義。
        _scriptNavigation = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        foreach (var item in _scriptMatches.ToolbarItems)
        {
            _scriptNavigation.Children.Add(item);
        }

        // 換行是一個維持著的狀態不是一次動作，所以是開關不是按鈕。
        _wrap = SqlAssistChrome.CreateIconToggle(SqlIcon.Wrap, "長文字換行顯示");
        _wrap.Focusable = false;
        _wrap.Checked += (_, _) => SqlAssistPlatformGuard.Run("預覽換行", () => ApplyWrap(true));
        _wrap.Unchecked += (_, _) => SqlAssistPlatformGuard.Run("預覽取消換行", () => ApplyWrap(false));

        // 一顆複製：有選取就複製選取，沒有就是完整的 CREATE 指令碼——那是按下去的人多半要的；
        // 「整個表格」這種少用的留在右鍵選單。
        var copy = SqlAssistChrome.CreateIconButton(SqlIcon.Copy, "複製選取內容；沒有選取時複製完整指令碼");
        copy.Focusable = false;
        copy.Click += (_, _) => SqlAssistPlatformGuard.Run("複製結構預覽", CopyCurrent);

        // 搜尋｜導覽｜換行與複製：搜尋改變「看哪些」，後面那一群改變「怎麼看、拿去哪」。
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(_searchBar);
        tools.Children.Add(SqlAssistChrome.CreateGroupDivider());
        tools.Children.Add(_scriptNavigation);
        tools.Children.Add(_wrap);
        tools.Children.Add(copy);
        SqlAssistChrome.SetTabStripTrailing(_tabs, tools);

        var header = new StackPanel { Margin = new Thickness(16, 12, 14, 10) };
        header.Children.Add(titleRow);
        header.Children.Add(_signature);
        header.Children.Add(_description);

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
        // 點進來才接受焦點，那時才需要能夠拉選文字或輸入搜尋字。
        Focusable = false;

        // 收起來就是這一次看完了：下一次打開是另一件事，留著上一次的搜尋字只會讓
        // 使用者以為那張表少了幾欄。
        IsVisibleChanged += (_, _) => SqlAssistPlatformGuard.Run("收起結構預覽的搜尋", () =>
        {
            if (!IsVisible)
            {
                ShellKeyCapture.End(this);
                ResetSearch(keepFocus: false);
            }
        });
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

    ITextView IShellKeyTarget.View => _view;

    UIElement IShellKeyTarget.Scope => _searchBar;

    IInputElement IShellKeyTarget.FocusTarget => _search;

    void IShellKeyTarget.Cancel() => HandleEscape();

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
        _partial = false;
        _scriptText = null;
        ResetContent();
        LeaveBuiltIn();
        SetTitle(objectInfo);
        ShowPills(pending: "載入中…");
        SetDescription(null);
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
    /// </remarks>
    private void SetTitle(SqlObjectInfo objectInfo)
    {
        SetKind(SqlIcons.GetMoniker(objectInfo.Kind), objectInfo.Kind.ToDisplayName(),
            SqlIcons.GetImageElement(objectInfo.Kind).AutomationName);

        _title.Inlines.Clear();

        // 指令碼自己宣告的名稱沒有結構描述，那時整段前綴都不寫：[].[#TempTest]
        // 宣稱有一個叫空字串的結構描述，而 [dbo].[@rows] 連文法都不成立。
        if (objectInfo.SchemaPrefix is { Length: > 0 } prefix)
        {
            _title.Inlines.Add(new Run(prefix)
                .WithTheme(Run.ForegroundProperty, ThemeBrush.DimForeground));
        }

        _title.Inlines.Add(new Run(objectInfo.QuotedName)
        {
            FontWeight = FontWeights.SemiBold
        });
    }

    /// <summary>種類膠囊：圖示與種類文字一起出現，辨識不只靠圖示。</summary>
    private void SetKind(Microsoft.VisualStudio.Imaging.Interop.ImageMoniker moniker, string name, string? automationName)
    {
        _kindIcon.Moniker = moniker;
        AutomationProperties.SetName(_kindIcon, automationName ?? name);
        _kindPill.Text = name;
        _kindPill.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 種類以外的那幾顆膠囊。
    /// </summary>
    /// <param name="primaryKey">主索引鍵的欄名；null 表示這一次不談主索引鍵。</param>
    /// <param name="noPrimaryKey">資料表確定沒有主索引鍵；那是一件值得說的事（堆積表）。</param>
    /// <param name="pending">還在載入的那一段；null 表示沒有東西在等。</param>
    private void ShowPills(string? primaryKey = null, bool noPrimaryKey = false, string? pending = null, bool failed = false)
    {
        if (primaryKey is not null)
        {
            _keyPill.Text = primaryKey;
            _keyPill.ToolTip = "主索引鍵：" + primaryKey + "（排序方向見索引分頁）";
            _keyPill.Tone = SqlPillTone.Accent;
        }
        else if (noPrimaryKey)
        {
            _keyPill.Text = "沒有主索引鍵";
            _keyPill.ToolTip = null;
            _keyPill.Tone = SqlPillTone.Neutral;
        }

        _keyPill.Visibility = Visible(primaryKey is not null || noPrimaryKey);
        _pendingPill.Text = pending ?? string.Empty;
        _pendingPill.Visibility = Visible(pending is not null);
        _failurePill.Visibility = Visible(failed);
    }

    /// <summary>
    /// 顯示一個內建名稱的完整說明：簽章、用途、範例，以及各引數的對照表。
    /// </summary>
    /// <remarks>
    /// 與物件結構共用同一個視窗殼層、同一套擺放、縮放與複製。內容則完全不同：
    /// 沒有查詢也沒有分層載入，資料是隨組件發布的一份，因此直接填完，不走
    /// 「只填看得見的分頁」那條路——那條路省的是查詢與版面計算，而這裡兩者都沒有。
    ///
    /// 範例沿用指令碼分頁：那是同一個唯讀的著色檢視，換一個標題就是了。
    /// </remarks>
    public void ShowBuiltIn(SqlBuiltInDoc doc)
    {
        _structure = null;
        _partial = false;
        _builtIn = doc;
        ResetContent();
        ClearTabs();

        SetKind(SqlIcons.GetMoniker(Kind(doc)), KindName(doc), SqlIcons.GetImageElement(Kind(doc)).AutomationName);
        ShowPills();

        _title.Inlines.Clear();
        _title.Inlines.Add(new Run(doc.Name) { FontWeight = FontWeights.SemiBold });

        _signature.Text = doc.Signature;
        _signature.Visibility = Visible(doc.Signature.Length > 0);
        SetDescription(doc.Summary);
        _status.Text = string.Empty;

        foreach (var tab in _gridTabs)
        {
            tab.Item.Visibility = Visibility.Collapsed;
        }

        _scriptText = doc.Example;
        _scriptHeader.Label = "範例";
        _scriptTab.Visibility = Visible(doc.Example.Length > 0);

        for (var index = 0; index < _referenceTabs.Count || index < doc.References.Count; index++)
        {
            var tab = EnsureReferenceTab(index);

            if (index >= doc.References.Count)
            {
                tab.Item.Visibility = Visibility.Collapsed;
                tab.Grid.ItemsSource = null;
                continue;
            }

            FillReference(tab, doc.References[index]);
        }

        // 落在對照表而不是範例：使用者是從提示點進來的，那段範例他剛剛才看過，
        // 而他要的是「style 到底有哪些」。沒有對照表時才退回範例。
        _tabs.SelectedItem = _referenceTabs.Count > 0 && doc.References.Count > 0
            ? _referenceTabs[0].Item
            : FirstVisibleTab();

        PopulateSelectedTab();
        UpdateSearchResults();
    }

    private void FillReference(ReferenceTab tab, SqlBuiltInReference reference)
    {
        tab.Header.Label = reference.Title;
        tab.Item.Visibility = Visibility.Visible;

        for (var column = 0; column < tab.Grid.Columns.Count; column++)
        {
            var used = column < reference.Columns.Count;
            tab.Grid.Columns[column].Header = used ? reference.Columns[column] : string.Empty;
            tab.Grid.Columns[column].Visibility = Visible(used);
        }

        var rows = new List<ReferenceRow>(reference.Rows.Count);

        foreach (var row in reference.Rows)
        {
            rows.Add(new ReferenceRow(row));
        }

        tab.Grid.ItemsSource = rows;
        tab.Header.ShowTotal(rows.Count);
        ApplyFilter(tab.Grid);
    }

    private ReferenceTab EnsureReferenceTab(int index)
    {
        if (index < _referenceTabs.Count)
        {
            return _referenceTabs[index];
        }

        var grid = CreateGrid(
            (string.Empty, nameof(ReferenceRow.Cell1)),
            new GridColumn(string.Empty, nameof(ReferenceRow.Cell2), GridColumn.TextWidth),
            new GridColumn(string.Empty, nameof(ReferenceRow.Cell3), GridColumn.TextWidth),
            new GridColumn(string.Empty, nameof(ReferenceRow.Cell4), GridColumn.TextWidth));
        ApplyGridMetrics(grid, SqlAssistChrome.CreateColumnHeaderStyle(_metrics));

        var header = new SqlTabHeader(string.Empty);
        var item = new TabItem
        {
            Header = header,
            Content = grid,
            Template = SqlAssistChrome.CreateTabItemTemplate(),
            Visibility = Visibility.Collapsed
        };

        _tabs.Items.Add(item);
        var tab = new ReferenceTab(item, header, grid);
        _referenceTabs.Add(tab);
        return tab;
    }

    // 圖示與那一行種類文字與滑鼠停留提示共用同一份對照（SqlBuiltInKinds）：
    // 兩個表面畫的是同一個名稱，分成兩份的症狀是改了一邊另一邊沒改。
    private static SuggestionKind Kind(SqlBuiltInDoc doc) => doc.Kind.ToSuggestionKind();

    private static string KindName(SqlBuiltInDoc doc) => doc.Kind.GetDisplayName();

    /// <summary>顯示一段訊息取代內容，例如沒有連線或這一項沒有結構。</summary>
    public void ShowMessage(string title, string message)
    {
        _structure = null;
        _partial = false;
        _scriptText = null;
        ResetContent();
        LeaveBuiltIn();

        // 沒有物件語意的訊息不顯示種類膠囊，避免誤認為未知種類的物件。
        _kindPill.Visibility = Visibility.Collapsed;
        ShowPills();
        _title.Inlines.Clear();
        _title.Inlines.Add(new Run(title));
        SetDescription(message);
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
        Populate(new SqlObjectStructure(detail, structurePending: true), partial: true);
    }

    public void Populate(SqlObjectStructure structure)
    {
        Populate(structure, partial: false);
    }

    private void Populate(SqlObjectStructure structure, bool partial)
    {
        _structure = structure;
        _partial = partial;
        _scriptText = null;
        ResetContent();
        LeaveBuiltIn();
        SetTitle(structure.Object);
        // 切頁可能同步回報顯示失敗，不能在填入之後再把那句訊息清掉。
        _status.Text = string.Empty;

        // 空的分頁留在畫面上只會讓人多點一次才知道沒東西。第四層還沒到齊時，
        // 靠它的那幾頁一律不顯示——空清單在那個時候不是答案。
        foreach (var tab in _gridTabs)
        {
            var count = tab.Count(structure);
            tab.Item.Visibility = Visible((!partial || !tab.RequiresStructure) && count > 0);
            tab.Header.ShowTotal(count);
        }

        _scriptTab.Visibility = Visible(!partial);

        if (_tabs.SelectedItem is not TabItem selected || selected.Visibility != Visibility.Visible)
        {
            _tabs.SelectedItem = FirstVisibleTab();
        }

        PopulateSelectedTab();
        UpdateSearchResults();
        ShowStructurePills(structure, partial);
        SetDescription(structure.Description);
    }

    private void ShowStructurePills(SqlObjectStructure structure, bool partial)
    {
        if (partial)
        {
            ShowPills(pending: "索引與外來鍵載入中…");
        }
        else if (structure.IsStructureUnavailable)
        {
            ShowPills(failed: true);
        }
        else if (structure.PrimaryKey is { } primaryKey)
        {
            ShowPills(primaryKey: primaryKey.DescribeKeyColumnNames());
        }
        else
        {
            ShowPills(noPrimaryKey: structure.Object.Kind == SqlObjectKind.Table);
        }
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
        _scriptNavigation.Visibility = Visible(ReferenceEquals(_tabs.SelectedItem, _scriptTab));
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
        if (_tabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        _scriptNavigation.Visibility = Visible(ReferenceEquals(tab, _scriptTab));

        if (_structure is null && _builtIn is null)
        {
            return;
        }

        if (!_populated.Add(tab))
        {
            return;
        }

        try
        {
            if (ReferenceEquals(tab, _scriptTab))
            {
                ShowScript();
            }
            else if (FindGridTab(tab) is { } grid && _structure is { } structure)
            {
                var rows = GetRows(grid, structure);
                grid.Grid.ItemsSource = rows;
                UpdateOptionalColumns(grid.Grid, rows);
                ApplyFilter(grid.Grid);
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

    /// <summary>指令碼連同命中一起換上；有命中就停在第一處。</summary>
    private void ShowScript()
    {
        var script = GetScript();
        var highlights = MatchHighlights.Locate(_matcher, script);
        _scriptMatches.Show(script, highlights);

        // 少標了一定要說，否則使用者按到最後一處就以為看完了。
        if (highlights.IsTruncated)
        {
            _status.Text = MatchHighlights.TruncatedNotice;
        }
    }

    private System.Collections.IEnumerable GetRows(GridTab tab, SqlObjectStructure structure)
    {
        if (!_rows.TryGetValue(tab, out var rows))
        {
            rows = tab.Rows(structure);
            _rows.Add(tab, rows);
        }

        return rows;
    }

    /// <summary>
    /// 把這一次整欄都是空的那些欄收掉。
    /// </summary>
    /// <remarks>
    /// 只在填入之後做一次，成本落在使用者真的切過去的那一個分頁上。讀不出繫結
    /// 路徑或那個屬性時一律保留整欄——把看不懂的東西藏起來，症狀會是「資料明明
    /// 查到了卻不見了」，那比多一個空欄糟得多。
    /// </remarks>
    private void UpdateOptionalColumns(DataGrid grid, System.Collections.IEnumerable rows)
    {
        foreach (var column in grid.Columns)
        {
            if (_optionalColumns.Contains(column))
            {
                column.Visibility = Visible(SqlDataGridText.HasAnyValue(column, rows));
            }
        }
    }

    private GridTab? FindGridTab(TabItem item)
    {
        foreach (var tab in _gridTabs)
        {
            if (ReferenceEquals(tab.Item, item))
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>標題底下那一行說明；沒有掛說明時整列收掉。</summary>
    /// <remarks>
    /// 收斂空白走 <see cref="SqlDescriptionText"/>：說明是使用者自己打進
    /// <c>sp_addextendedproperty</c> 的字串，帶換行的那一段會把這一行撐成好幾行，
    /// 而標題列的高度是浮動視窗量出來的。全文仍讀得到——這一行的 Tooltip
    /// 綁在自己的文字上。
    /// </remarks>
    private void SetDescription(string? description)
    {
        var text = SqlDescriptionText.Collapse(description);
        _description.Text = text ?? string.Empty;
        _description.Visibility = Visible(text is not null);
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

    /// <summary>換一份內容之前丟掉上一份留下的東西：填過的分頁、建好的資料列與簽章。</summary>
    private void ResetContent()
    {
        _populated.Clear();
        _rows.Clear();
        _signature.Visibility = Visibility.Collapsed;
    }

    private void ClearTabs()
    {
        foreach (var tab in _gridTabs)
        {
            tab.Grid.ItemsSource = null;
            tab.Header.ShowTotal(null);
        }

        _scriptHeader.ShowTotal(null);
        _scriptMatches.Clear();
    }

    /// <summary>
    /// 換回資料庫物件那一組內容。
    /// </summary>
    /// <remarks>
    /// 對照表分頁收掉、指令碼分頁的標題換回來。少了這一步的症狀是看過一次
    /// <c>CONVERT</c> 之後，接下來每一個資料表都帶著一個「style（日期時間）」分頁。
    /// </remarks>
    private void LeaveBuiltIn()
    {
        if (_builtIn is null)
        {
            return;
        }

        _builtIn = null;
        _scriptHeader.Label = "指令碼";

        foreach (var tab in _referenceTabs)
        {
            tab.Item.Visibility = Visibility.Collapsed;
            tab.Grid.ItemsSource = null;
        }
    }

    private string GetScript()
    {
        if (_scriptText is not null || _structure is null)
        {
            return _scriptText ?? string.Empty;
        }

        // 使用者切到指令碼分頁、按下複製或搜尋才會走到這裡，因此是 User。裡面還有一則
        // 「執行結構健檢」——那一段才是真正花時間的部分。
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.GeneratingObjectScript,
            NotificationKind.Preview, NotificationOrigin.User, NotificationLevel.Info,
            _structure.Object.QualifiedName);
        return _scriptText = _structure.BuildScript(
            SqlScriptPreferences.Create(Environment.NewLine, _structure.Object));
    }

    private void OnSearchTextChanged()
    {
        _clearSearch.IsEnabled = _search.Text.Length > 0;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    /// <summary>
    /// 搜尋框握著鍵盤的期間，殼層解析成編輯器命令的按鍵交還給它。
    /// </summary>
    /// <remarks>
    /// Backspace、Delete、方向鍵與 Enter 在「文字編輯器」範圍都有繫結，殼層照作用中的
    /// 查詢視窗把它們送進命令鏈；不接回來的話，在搜尋框裡按 Backspace 刪的是後面那份 SQL。
    /// </remarks>
    private void OnSearchFocusChanged()
    {
        if (_search.IsKeyboardFocusWithin)
        {
            ShellKeyCapture.Begin(this);
        }
        else
        {
            ShellKeyCapture.End(this);
        }
    }

    /// <summary>清掉搜尋字，立刻還原所有分頁；不等去彈跳。</summary>
    private void ResetSearch(bool keepFocus)
    {
        _searchDelay.Stop();

        if (_search.Text.Length > 0)
        {
            _search.Clear();
            _searchDelay.Stop();
        }

        if (_matcher is not null)
        {
            ApplySearch();
        }

        if (keepFocus)
        {
            _search.Focus();
        }
    }

    /// <summary>
    /// 套用搜尋字：資料格只留符合的列並標出命中，指令碼標出每一處，分頁換成命中數。
    /// </summary>
    /// <remarks>
    /// 資料格用篩選不用導覽：一百多欄的表只標出命中而不篩，使用者照樣要一路捲下去找。
    /// 指令碼反過來，篩掉幾行就讀不懂了，所以是高亮加上一處一處走。
    /// 比對走 <see cref="TextMatcher"/> 的字面比對、不分大小寫，與 SQL Search 的本文同一套。
    /// </remarks>
    private void ApplySearch()
    {
        var pattern = _search.Text.Trim();
        _matcher = pattern.Length == 0 ? null : new TextMatcher(pattern, TextMatchOptions.None);

        // 儲存格裡的高亮讀的是繼承下去的比對器，換一個值整個視窗的格子一起重畫。
        SqlHighlightText.SetMatcher(_tabs, _matcher);

        foreach (var (_, grid, _) in SearchableGrids())
        {
            if (grid.ItemsSource is not null)
            {
                ApplyFilter(grid);
            }
        }

        if (_populated.Contains(_scriptTab))
        {
            _status.Text = string.Empty;
            ShowScript();
        }

        UpdateSearchResults();
    }

    private void ApplyFilter(DataGrid grid)
    {
        grid.Items.Filter = _matcher is { } matcher ? SqlDataGridText.CreateFilter(grid, matcher) : null;
    }

    /// <summary>
    /// 分頁上的數字：沒有搜尋時是總數，有搜尋時是命中數。
    /// </summary>
    /// <remarks>
    /// 還沒切過去的分頁也要數：命中落在哪一頁正是使用者要從分頁上讀到的事。資料列建一次
    /// 就留著給填入用；指令碼只在搜尋時才組，那時使用者本來就在找東西。
    /// </remarks>
    private void UpdateSearchResults()
    {
        if (_matcher is not { } matcher)
        {
            RestoreTotals();
            return;
        }

        foreach (var tab in _gridTabs)
        {
            if (tab.Item.Visibility == Visibility.Visible && _structure is { } structure)
            {
                // 還沒切過去的分頁，欄的收合還停在上一個物件；先照這一份資料收好，
                // 否則上一張表沒有說明、這一張有，說明裡的命中就數不到。
                var rows = GetRows(tab, structure);
                UpdateOptionalColumns(tab.Grid, rows);
                tab.Header.ShowHits(CountMatches(tab.Grid, rows, matcher));
            }
        }

        foreach (var tab in _referenceTabs)
        {
            if (tab.Item.Visibility == Visibility.Visible && tab.Grid.ItemsSource is { } rows)
            {
                tab.Header.ShowHits(CountMatches(tab.Grid, rows, matcher));
            }
        }

        if (_scriptTab.Visibility == Visibility.Visible)
        {
            _scriptHeader.ShowHits(MatchHighlights.Locate(matcher, GetScript()).Count);
        }
    }

    private void RestoreTotals()
    {
        if (_structure is { } structure)
        {
            foreach (var tab in _gridTabs)
            {
                tab.Header.ShowTotal(tab.Count(structure));
            }
        }

        foreach (var tab in _referenceTabs)
        {
            tab.Header.ShowTotal(tab.Grid.ItemsSource is ICollection<ReferenceRow> rows ? rows.Count : null);
        }

        _scriptHeader.ShowTotal(null);
    }

    private static int CountMatches(DataGrid grid, System.Collections.IEnumerable rows, TextMatcher matcher)
    {
        var filter = SqlDataGridText.CreateFilter(grid, matcher);
        var count = 0;

        foreach (var row in rows)
        {
            if (filter(row))
            {
                count++;
            }
        }

        return count;
    }

    private IEnumerable<(TabItem Item, DataGrid Grid, SqlTabHeader Header)> SearchableGrids()
    {
        foreach (var tab in _gridTabs)
        {
            yield return (tab.Item, tab.Grid, tab.Header);
        }

        foreach (var tab in _referenceTabs)
        {
            yield return (tab.Item, tab.Grid, tab.Header);
        }
    }

    /// <summary>
    /// 換行開關：自由文字欄折成多行、列高跟著內容長，指令碼一起換行。
    /// </summary>
    /// <remarks>
    /// 只換儲存格樣板與列高，不重建資料格：選取、捲動位置與收起的空欄都留著。
    /// 名稱與型別不跟著換行——那幾欄折起來只會讓一列變成三行而讀不出是同一列。
    /// </remarks>
    private void ApplyWrap(bool wrap)
    {
        foreach (var column in _freeTextColumns)
        {
            column.CellTemplate = CreateCellTemplate(column.SortMemberPath, wrap ? _cellWrapped : _cellLine);
        }

        foreach (var (_, grid, _) in SearchableGrids())
        {
            ApplyRowHeight(grid);
        }

        _script.SetWrap(wrap);
    }

    private void ApplyRowHeight(DataGrid grid)
    {
        grid.MinRowHeight = _metrics.RowHeight;
        grid.RowHeight = _wrap.IsChecked == true ? double.NaN : _metrics.RowHeight;
    }

    /// <summary>Esc：搜尋框裡有字時先清字，第二次才關視窗。</summary>
    private void HandleEscape()
    {
        if (_search.IsKeyboardFocusWithin && _search.Text.Length > 0)
        {
            ResetSearch(keepFocus: true);
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
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
            var selected = _script.SelectedSql;
            Copy(
                selected.Length == 0 ? GetScript() : selected,
                selected.Length == 0 ? "沒有選取，已複製完整指令碼。" : "已複製選取的指令碼。");
            return;
        }

        if (tab.Content is DataGrid grid)
        {
            var text = SqlDataGridText.Build(grid, selectedOnly: true);

            if (string.IsNullOrEmpty(text))
            {
                _status.Text = "請先在表格裡選取要複製的儲存格。";
                return;
            }

            Copy(text, "已複製選取的儲存格。");
        }
    }

    /// <summary>分頁列上那一顆複製：有選取複製選取，沒有就是完整指令碼。</summary>
    private void CopyCurrent()
    {
        if (HasSelection())
        {
            CopySelection();
        }
        else
        {
            CopyAll();
        }
    }

    /// <summary>複製整份指令碼，與目前在哪個分頁無關。</summary>
    /// <remarks>
    /// 內建說明沒有指令碼，那時「全部」指的是目前這張對照表——十六列 style
    /// 正是使用者會想貼到別處留著的東西。
    /// </remarks>
    public void CopyAll()
    {
        if (_builtIn is not null)
        {
            if (_tabs.SelectedItem is TabItem { Content: DataGrid })
            {
                CopyGridAll();
                return;
            }

            Copy(GetScript(), "已複製範例。");
            return;
        }

        Copy(GetScript(), _structure is { CanBuildExecutableScript: true }
            ? "已複製完整指令碼到剪貼簿。"
            : "已複製結構說明；完整可執行指令碼目前不可用。");
    }

    private void CopyGridAll()
    {
        if (_tabs.SelectedItem is TabItem { Content: DataGrid grid })
        {
            Copy(SqlDataGridText.Build(grid, selectedOnly: false), "已複製整個表格。");
        }
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
    public void PlayAppear() => SqlAssistChrome.PlayAppear(_root);

    /// <summary>目前分頁有沒有選取的內容；決定 Ctrl+C 該不該由預覽接手。</summary>
    public bool HasSelection()
    {
        if (_tabs.SelectedItem is not TabItem tab)
        {
            return false;
        }

        if (ReferenceEquals(tab, _scriptTab))
        {
            return _script.HasSelection;
        }

        return tab.Content is DataGrid grid && grid.SelectedCells.Count > 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs eventArgs)
    {
        // 焦點在預覽裡時，編輯器的命令處理常式收不到按鍵，這幾個得由這裡處理。
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            HandleEscape();
            return;
        }

        var modifiers = eventArgs.KeyboardDevice.Modifiers;

        if (_search.IsKeyboardFocusWithin)
        {
            // Enter 在指令碼裡一處一處走；資料格的命中已經篩在眼前，沒有「下一處」可走。
            if (eventArgs.Key == Key.Enter && ReferenceEquals(_tabs.SelectedItem, _scriptTab))
            {
                eventArgs.Handled = true;
                _scriptMatches.Move(forward: (modifiers & ModifierKeys.Shift) == 0);
            }

            // 其餘按鍵（包括 Ctrl+C）屬於文字方塊自己。
            base.OnPreviewKeyDown(eventArgs);
            return;
        }

        if (eventArgs.Key == Key.F && modifiers == ModifierKeys.Control)
        {
            eventArgs.Handled = true;
            _search.Focus();
            _search.SelectAll();
            return;
        }

        if (eventArgs.Key == Key.C && (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            eventArgs.Handled = true;
            CopySelection();
            return;
        }

        base.OnPreviewKeyDown(eventArgs);
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

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
    ///
    /// 文字欄一律是樣板欄加 <see cref="SqlHighlightText"/>：搜尋時每一格自己標出命中。
    /// <see cref="DataGridColumn.SortMemberPath"/> 指向同一個屬性，複製、排序與空欄判斷
    /// 讀的都是它（見 <see cref="SqlDataGridText"/>）。
    /// </remarks>
    private DataGrid CreateGrid(params GridColumn[] columns)
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

        foreach (var column in columns)
        {
            var text = new DataGridTemplateColumn
            {
                Header = column.Header,
                SortMemberPath = column.Path,
                CellTemplate = CreateCellTemplate(column.Path, _cellLine),
                Width = column.Fill ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto
            };

            if (column.Fill)
            {
                text.MinWidth = GridColumn.FillMinimumWidth;
            }
            else if (column.MaximumWidth > 0)
            {
                text.MaxWidth = column.MaximumWidth;
            }

            if (column.IsFreeText)
            {
                _freeTextColumns.Add(text);
            }

            if (column.Optional)
            {
                _optionalColumns.Add(text);
            }

            grid.Columns.Add(text);
        }

        return grid;
    }

    private static DataTemplate CreateCellTemplate(string path, Style style)
    {
        var text = new FrameworkElementFactory(typeof(SqlHighlightText));
        text.SetBinding(SqlHighlightText.SourceTextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        text.SetValue(StyleProperty, style);
        return new DataTemplate { VisualTree = text };
    }

    /// <summary>
    /// 儲存格文字的兩種樣式：單行省略，或整段換行。
    /// </summary>
    /// <remarks>
    /// 以共用的儲存格樣式為底（內距、垂直置中），只換斷行與 Tooltip。Tooltip 讀
    /// <see cref="SqlHighlightText.SourceText"/> 而不是 <see cref="TextBlock.Text"/>：
    /// 高亮是用 Run 組起來的，那時 Text 不是完整的一格。
    /// </remarks>
    private static Style CreateCellStyle(bool wrap)
    {
        var style = new Style(typeof(SqlHighlightText), SqlAssistChrome.CreateCellTextStyle());
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(ToolTipProperty,
            new Binding(nameof(SqlHighlightText.SourceText)) { RelativeSource = RelativeSource.Self }));

        if (wrap)
        {
            // 換行之後一格可能好幾行；上下留一點距離，列與列之間才分得開。
            style.Setters.Add(new Setter(MarginProperty, new Thickness(10, 4, 10, 4)));
        }

        return style;
    }

    /// <summary>
    /// 把旗標畫成一列膠囊的欄。
    /// </summary>
    /// <remarks>
    /// <see cref="DataGridColumn.SortMemberPath"/> 不是為了排序才設的——這一欄不是文字欄，
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
    /// 那兩樣只能整個換掉。指令碼分頁不動，它跟的是編輯器的字型與字級；分頁列右側
    /// 的工具也不動，理由見 <see cref="ToolMetrics"/>。
    /// </remarks>
    public void ApplyFontSize(double baseSize)
    {
        if (Math.Abs(_fontSize - baseSize) < 0.01)
        {
            return;
        }

        _fontSize = baseSize;
        _metrics = new SqlAssistChrome.Metrics(baseSize);

        _title.FontSize = _metrics.Title;
        _signature.FontSize = _metrics.Caption;
        _description.FontSize = _metrics.Caption;
        _status.FontSize = _metrics.Caption;
        _tabs.FontSize = _metrics.Body;

        foreach (var pill in new[] { _kindPill, _keyPill, _pendingPill, _failurePill })
        {
            pill.TextSize = _metrics.Caption;
        }

        var headerStyle = SqlAssistChrome.CreateColumnHeaderStyle(_metrics);

        foreach (var (_, grid, _) in SearchableGrids())
        {
            ApplyGridMetrics(grid, headerStyle);
        }

        _flags.CellTemplate = PreviewChrome.CreateFlagsCellTemplate(
            nameof(ColumnRow.FlagList),
            _metrics);
    }

    private void ApplyGridMetrics(DataGrid grid, Style headerStyle)
    {
        grid.FontSize = _metrics.Body;
        grid.ColumnHeaderStyle = headerStyle;
        ApplyRowHeight(grid);
    }

    public void Dispose()
    {
        _searchDelay.Stop();
        ShellKeyCapture.End(this);
        _script.Dispose();
    }
}
