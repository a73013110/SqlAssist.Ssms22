using System;
using System.Collections.Generic;
using SqlAssist.Core.Tabular;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 結構描述的 Excel 版面：一頁總覽，每張資料表一頁。
/// </summary>
/// <remarks>
/// 總覽每一列連到那張表的那一頁，每一頁左上角連回總覽：一百頁的活頁簿只靠底下的頁籤找不到東西。
/// 表頭凍結並開好自動篩選——拿到這份檔案的人第一件事通常是篩「說明是空的」或某個型別。
/// 頁名用資料表名稱，不同結構描述有同名的表時才帶結構描述；Excel 的長度與字元限制由
/// <see cref="XlsxSheetNames"/> 處理，撞名照規則加序號。
/// </remarks>
public static class SqlSchemaWorkbook
{
    /// <summary>總覽的表頭在第幾列；上面是標題與脈絡。</summary>
    internal const int OverviewHeaderRow = 4;

    /// <summary>每一頁資料表的表頭在第幾列；上面是返回連結、標題、說明與脈絡。</summary>
    internal const int TableHeaderRow = 6;

    public static XlsxWorkbook Build(SqlSchemaDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var workbook = new XlsxWorkbook();
        var overview = workbook.AddSheet(SchemaExportText.OverviewSheet);
        var duplicated = DuplicatedNames(document.Tables);

        var sheets = new List<XlsxSheet>(document.Tables.Count);
        foreach (var table in document.Tables)
        {
            sheets.Add(workbook.AddSheet(duplicated.Contains(table.Object.Name) ? table.Object.DisplayName : table.Object.Name));
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
        sheet.SetText(2, 1, document.Meta, XlsxStyle.Dim);

        var headers = new[]
        {
            SchemaExportText.HeaderNumber, SchemaExportText.HeaderTable, SchemaExportText.HeaderKind, SchemaExportText.HeaderDescription,
            SchemaExportText.HeaderColumns, SchemaExportText.HeaderDescribed, SchemaExportText.HeaderRows,
        };
        Header(sheet, OverviewHeaderRow, headers, new[] { 6d, 34, 9, 60, 9, 9, 12 });

        for (var i = 0; i < document.Tables.Count; i++)
        {
            var table = document.Tables[i];
            var row = OverviewHeaderRow + 1 + i;
            sheet.SetNumber(row, 1, i + 1, XlsxStyle.Center);
            sheet.SetLink(row, 2, table.Object.DisplayName, tables[i]);
            sheet.SetText(row, 3, table.Kind);
            sheet.SetText(row, 4, table.Description);
            sheet.SetNumber(row, 5, table.Columns.Count);
            sheet.SetNumber(row, 6, table.Described);
            if (table.Rows is { } rows) sheet.SetNumber(row, 7, rows);
        }

        Finish(sheet, OverviewHeaderRow, document.Tables.Count, headers.Length);
    }

    private static void WriteTable(XlsxSheet sheet, SqlSchemaTable table, XlsxSheet overview)
    {
        sheet.SetLink(1, 1, SchemaExportText.BackToOverview, overview);
        sheet.SetText(2, 1, table.Object.DisplayName, XlsxStyle.Title);
        sheet.SetText(3, 1, table.Description, XlsxStyle.Plain);
        sheet.SetText(4, 1, table.Meta, XlsxStyle.Dim);

        var headers = new[]
        {
            SchemaExportText.HeaderNumber, SchemaExportText.HeaderColumn, SchemaExportText.HeaderType, SchemaExportText.HeaderNullable,
            SchemaExportText.HeaderKey, SchemaExportText.HeaderDefault, SchemaExportText.HeaderTraits, SchemaExportText.HeaderReference,
            SchemaExportText.HeaderDescription,
        };
        Header(sheet, TableHeaderRow, headers, new[] { 6d, 28, 18, 7, 8, 18, 22, 30, 60 });

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var row = TableHeaderRow + 1 + i;
            sheet.SetNumber(row, 1, column.Ordinal, XlsxStyle.Center);
            sheet.SetText(row, 2, column.Column.Name);
            sheet.SetText(row, 3, column.Column.DataType, XlsxStyle.Code);
            sheet.SetText(row, 4, column.Column.IsNullable ? SqlSchemaMarkdown.Yes : null, XlsxStyle.Center);
            sheet.SetText(row, 5, column.Key, XlsxStyle.Center);
            sheet.SetText(row, 6, column.Default, XlsxStyle.Code);
            sheet.SetText(row, 7, column.Traits);
            sheet.SetText(row, 8, column.Reference, XlsxStyle.Code);
            sheet.SetText(row, 9, column.Description);
        }

        Finish(sheet, TableHeaderRow, table.Columns.Count, headers.Length);
    }

    private static void Header(XlsxSheet sheet, int row, IReadOnlyList<string> headers, IReadOnlyList<double> widths)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            sheet.SetText(row, i + 1, headers[i], XlsxStyle.Header);
            sheet.SetWidth(i + 1, widths[i]);
        }
    }

    /// <summary>表頭以上凍結，表頭到最後一列開自動篩選；一列都沒有時也篩到表頭那一列。</summary>
    private static void Finish(XlsxSheet sheet, int headerRow, int rows, int columns)
    {
        sheet.FrozenRows = headerRow;
        sheet.SetFilter(headerRow, 1, headerRow + Math.Max(rows, 0), columns);
    }
}
