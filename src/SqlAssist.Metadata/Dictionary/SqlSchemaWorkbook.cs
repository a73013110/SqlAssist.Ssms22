using System;
using System.Collections.Generic;
using SqlAssist.Core.Tabular;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 結構描述的 Excel 版面：一頁總覽，每張資料表一頁。
/// </summary>
/// <remarks>
/// 總覽每一列連到那張表的那一頁，每一頁左上角連回總覽：一百頁的活頁簿只靠底下的頁籤找不到東西。
/// 表頭凍結並開好自動篩選——拿到這份檔案的人第一件事通常是篩「描述是空的」或某個型別。
/// 頁名用資料表名稱，不同結構描述有同名的表時才帶結構描述；Excel 的長度與字元限制由
/// <see cref="XlsxSheetNames"/> 處理，撞名照規則加序號。
///
/// 版面像一份文件而不是一張方格紙：關掉格線，表頭下緣一條深色細線，表身隔列淡底（篩選、排序後仍交替）；
/// 欄寬照內容收，空著的欄只有表頭那麼寬，最右邊那一欄長描述進得了第一個畫面；每一列上下留白。
/// 標題大字、脈絡淡色，表頭上方空一條細列分開；開檔時游標停在第一筆。
/// 總覽的頁籤上色，一百個頁籤裡一眼找得到回去的那一個。列印時橫向、寬度縮成一頁。
/// </remarks>
public static class SqlSchemaWorkbook
{
    /// <summary>總覽的表頭在第幾列；上面是標題、脈絡與一條細的空白列。</summary>
    internal const int OverviewHeaderRow = 4;

    private const double TitleHeight = 34;
    private const double MetaHeight = 22;
    private const double SpacerHeight = 10;
    private const double HeaderHeight = 28;
    private const double NavigationHeight = 24;

    /// <summary>表身一行字的高（12 點字）與上下合起來的留白；單行的列是 25.5 點。</summary>
    private const double LineHeight = 16.5;

    private const double RowPadding = 9;

    /// <summary>描述欄的寬度上限；再寬一行就長到讀的時候眼睛要橫掃。</summary>
    private const double DescriptionWidth = 60;

    /// <summary>總覽頁籤的顏色；與連結同一個藍。</summary>
    private const string OverviewTabColor = "FF0066CC";

    public static XlsxWorkbook Build(SqlSchemaDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var workbook = new XlsxWorkbook();
        var overview = workbook.AddSheet(SchemaExportText.OverviewSheet);
        overview.TabColor = OverviewTabColor;
        var duplicated = DuplicatedNames(document.Tables);

        var sheets = new List<XlsxSheet>(document.Tables.Count);
        foreach (var table in document.Tables)
        {
            sheets.Add(workbook.AddSheet(duplicated.Contains(table.Object.Name) ? table.Object.DisplayName : table.Object.Name));
        }

        foreach (var sheet in workbook.Sheets)
        {
            sheet.ShowGridLines = false;
            sheet.PrintLandscapeFitWidth = true;
        }

        WriteOverview(overview, document, sheets);
        for (var i = 0; i < document.Tables.Count; i++) WriteTable(sheets[i], document.Tables[i], overview);
        return workbook;
    }

    private static HashSet<string> DuplicatedNames(IReadOnlyList<SqlSchemaTable> tables)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (!seen.Add(table.Object.Name)) duplicated.Add(table.Object.Name);
        }

        return duplicated;
    }

    private static void WriteOverview(XlsxSheet sheet, SqlSchemaDocument document, IReadOnlyList<XlsxSheet> tables)
    {
        sheet.SetText(1, 1, SchemaExportText.Title(document.DatabaseName), XlsxStyle.Title);
        sheet.SetHeight(1, TitleHeight);
        sheet.SetText(2, 1, document.Meta, XlsxStyle.Dim);
        sheet.SetHeight(2, MetaHeight);
        sheet.SetHeight(3, SpacerHeight);

        var columns = new List<Column>
        {
            Column.Ordinal(SchemaExportText.HeaderNumber),
            Column.Text(SchemaExportText.HeaderTable, 16, 44),
            Column.Centered(SchemaExportText.HeaderKind),
            Column.Centered(SchemaExportText.HeaderColumns),
            Column.Centered(SchemaExportText.HeaderDescribed),
        };
        if (document.IncludesRows) columns.Add(Column.Centered(SchemaExportText.HeaderRows));
        columns.Add(Column.Text(SchemaExportText.HeaderDescription, 24, DescriptionWidth));

        for (var i = 0; i < document.Tables.Count; i++)
        {
            var table = document.Tables[i];
            var row = OverviewHeaderRow + 1 + i;
            var column = 1;
            sheet.SetNumber(row, column++, i + 1, XlsxStyle.Ordinal);
            sheet.SetLink(row, column++, table.Object.DisplayName, tables[i]);
            sheet.SetText(row, column++, table.Kind, XlsxStyle.Center);
            sheet.SetNumber(row, column++, table.Columns.Count);
            sheet.SetNumber(row, column++, table.Described);
            if (document.IncludesRows)
            {
                if (table.Rows is { } rows) sheet.SetNumber(row, column, rows);
                column++;
            }

            sheet.SetText(row, column, table.Description);
        }

        Lay(sheet, OverviewHeaderRow, document.Tables.Count, columns);
    }

    /// <summary>
    /// 一張資料表一頁：返回連結、標題、描述（有才寫）、脈絡，空一條細列之後是表頭。
    /// </summary>
    /// <remarks>描述空著時不留一整列空白：脈絡往上補，表頭跟著上移，凍結與篩選照實際的那一列。</remarks>
    private static void WriteTable(XlsxSheet sheet, SqlSchemaTable table, XlsxSheet overview)
    {
        var row = 1;
        sheet.SetLink(row, 1, SchemaExportText.BackToOverview, overview, XlsxStyle.Navigation);
        sheet.SetHeight(row++, NavigationHeight);
        sheet.SetText(row, 1, table.Object.DisplayName, XlsxStyle.Title);
        sheet.SetHeight(row++, TitleHeight);
        if (!string.IsNullOrEmpty(table.Description))
        {
            sheet.SetText(row, 1, table.Description, XlsxStyle.Plain);
            sheet.SetHeight(row++, MetaHeight);
        }

        sheet.SetText(row, 1, table.Meta, XlsxStyle.Dim);
        sheet.SetHeight(row++, MetaHeight);
        sheet.SetHeight(row++, SpacerHeight);
        var headerRow = row;

        var columns = new[]
        {
            Column.Ordinal(SchemaExportText.HeaderNumber),
            Column.Text(SchemaExportText.HeaderColumn, 14, 36),
            Column.Text(SchemaExportText.HeaderType, 12, 28),
            Column.Centered(SchemaExportText.HeaderNullable),
            Column.Centered(SchemaExportText.HeaderKey),
            Column.Text(SchemaExportText.HeaderDefault, 8, 28),
            Column.Text(SchemaExportText.HeaderTraits, 8, 24),
            Column.Text(SchemaExportText.HeaderReference, 8, 36),
            Column.Text(SchemaExportText.HeaderDescription, 24, DescriptionWidth),
        };

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            row = headerRow + 1 + i;
            sheet.SetNumber(row, 1, column.Ordinal, XlsxStyle.Ordinal);
            sheet.SetText(row, 2, column.Column.Name);
            sheet.SetText(row, 3, column.Column.DataType, XlsxStyle.Code);
            sheet.SetText(row, 4, column.Column.IsNullable ? SqlSchemaMarkdown.Yes : null, XlsxStyle.Center);
            sheet.SetText(row, 5, column.Key, XlsxStyle.Center);
            sheet.SetText(row, 6, column.Default, XlsxStyle.Code);
            sheet.SetText(row, 7, column.Traits);
            sheet.SetText(row, 8, column.Reference, XlsxStyle.Code);
            sheet.SetText(row, 9, column.Description);
        }

        Lay(sheet, headerRow, table.Columns.Count, columns);
    }

    /// <summary>
    /// 表頭、凍結、篩選、隔列淡底、欄寬、列高與開檔游標；表身要先寫好，欄寬與列高照內容估。
    /// </summary>
    private static void Lay(XlsxSheet sheet, int headerRow, int rows, IReadOnlyList<Column> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            sheet.SetText(headerRow, i + 1, columns[i].Header, columns[i].IsCentered ? XlsxStyle.HeaderCenter : XlsxStyle.Header);
        }

        sheet.SetHeight(headerRow, HeaderHeight);
        sheet.FrozenRows = headerRow;
        var lastRow = headerRow + Math.Max(rows, 0);
        sheet.SetFilter(headerRow, 1, lastRow, columns.Count);
        sheet.SetBanding(headerRow + 1, lastRow, columns.Count);

        for (var i = 0; i < columns.Count; i++) sheet.FitWidth(i + 1, columns[i].MinWidth, columns[i].MaxWidth);
        sheet.FitHeights(headerRow + 1, lastRow, LineHeight, RowPadding);
        sheet.SelectedCell = (headerRow + 1, 2);
    }

    /// <summary>一欄的表頭、對齊與欄寬範圍。</summary>
    private sealed class Column
    {
        private Column(string header, bool centered, double minWidth, double maxWidth)
        {
            Header = header;
            IsCentered = centered;
            MinWidth = minWidth;
            MaxWidth = maxWidth;
        }

        public string Header { get; }

        /// <summary>序號、記號與數字這幾欄：表頭與值都置中。</summary>
        public bool IsCentered { get; }

        public double MinWidth { get; }

        public double MaxWidth { get; }

        public static Column Ordinal(string header) => new(header, true, 6, 8);

        public static Column Centered(string header) => new(header, true, 7, 16);

        public static Column Text(string header, double min, double max) => new(header, false, min, max);
    }
}
