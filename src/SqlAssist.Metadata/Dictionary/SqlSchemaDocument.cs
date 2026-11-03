using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using SqlAssist.Core.Tabular;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>匯出結構描述的檔案格式。</summary>
public enum SqlSchemaExportFormat
{
    /// <summary>Excel 活頁簿：一頁總覽，每張資料表一頁，互相連結、表頭凍結可篩選。</summary>
    Excel,

    /// <summary>一份 Markdown：目錄加每張資料表一節，放得進 Git 與 Wiki。</summary>
    Markdown
}

/// <summary>匯出文件裡的一個資料行：字典的那一份，加上匯出時才讀的預設值、計算式與參照。</summary>
public sealed class SqlSchemaColumn
{
    internal SqlSchemaColumn(int ordinal, SqlDictionaryColumn column, string description, string? defaultValue, string? computed, string? reference)
    {
        Ordinal = ordinal;
        Column = column;
        Description = description;
        Default = defaultValue;
        Computed = computed;
        Reference = reference;
    }

    /// <summary>從 1 數的順序；<c>column_id</c> 刪過資料行會跳號，文件裡要連續。</summary>
    public int Ordinal { get; }

    public SqlDictionaryColumn Column { get; }

    /// <summary>匯出那一刻畫面上的說明（含還沒套用的編輯）。</summary>
    public string Description { get; }

    public string? Default { get; }

    public string? Computed { get; }

    /// <summary>外部索引鍵指向的 <c>schema.table.column</c>。</summary>
    public string? Reference { get; }

    /// <summary>鍵那一格：<c>PK</c>、<c>FK</c> 或兩個都是。</summary>
    public string Key => Column.IsPrimaryKey && Reference is not null ? "PK, FK"
        : Column.IsPrimaryKey ? "PK"
        : Reference is not null ? "FK"
        : "";

    /// <summary>識別與計算式；都不是時留空。</summary>
    public string Traits
    {
        get
        {
            var traits = new List<string>(2);
            if (Column.IsIdentity) traits.Add(SchemaExportText.Identity);
            if (Computed is not null) traits.Add(SchemaExportText.Computed(Computed));
            return string.Join("\n", traits);
        }
    }
}

/// <summary>匯出文件裡的一張資料表或一個檢視。</summary>
public sealed class SqlSchemaTable
{
    internal SqlSchemaTable(SqlDictionaryObject item, string description, long? rows, IReadOnlyList<SqlSchemaColumn> columns)
    {
        Object = item;
        Description = description;
        Rows = rows;
        Columns = columns;

        var described = 0;
        foreach (var column in columns)
        {
            if (column.Description.Length > 0) described++;
        }

        Described = described;
    }

    public SqlDictionaryObject Object { get; }

    public string Description { get; }

    /// <summary>資料列數（<c>sys.partitions</c> 的估計）；檢視與讀不到時為 null。</summary>
    public long? Rows { get; }

    public IReadOnlyList<SqlSchemaColumn> Columns { get; }

    public int Described { get; }

    public string Kind => Object.Kind == SqlDictionaryObjectKind.View ? SchemaExportText.KindView : SchemaExportText.KindTable;

    /// <summary>標題下那一行淡色的脈絡。</summary>
    public string Meta => Rows is { } rows
        ? SchemaExportText.TableMetaRows(Kind, Columns.Count, Described, rows)
        : SchemaExportText.TableMeta(Kind, Columns.Count, Described);
}

/// <summary>
/// 一份要匯出的結構描述：選定的那幾張表、它們的資料行與說明，以及匯出的時間與來源。
/// </summary>
/// <remarks>
/// 說明照<b>畫面上</b>的那一份（<paramref name="currentDescription"/>，含還沒套用的編輯）：使用者剛寫好一批說明，
/// 匯出來要給人看的就是那一批；要與伺服器一致，先套用再匯出。兩種格式讀同一份，欄位與順序只在這裡決定一次。
/// </remarks>
public sealed class SqlSchemaDocument
{
    private SqlSchemaDocument(string serverName, string databaseName, DateTime exportedAt, IReadOnlyList<SqlSchemaTable> tables)
    {
        ServerName = serverName;
        DatabaseName = databaseName;
        ExportedAt = exportedAt;
        Tables = tables;
    }

    public string ServerName { get; }

    public string DatabaseName { get; }

    public DateTime ExportedAt { get; }

    public IReadOnlyList<SqlSchemaTable> Tables { get; }

    /// <summary>總覽那一行淡色的脈絡。</summary>
    public string Meta => SchemaExportText.Meta(ServerName, DatabaseName,
        ExportedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Tables.Count);

    /// <param name="currentDescription">目前的說明：目標與載入時的值進來，含編輯的值出去。</param>
    /// <param name="objects">要匯出的物件，照這個順序。</param>
    public static SqlSchemaDocument Build(
        string serverName,
        string databaseName,
        IEnumerable<SqlDictionaryObject> objects,
        Func<SqlDescriptionTarget, string, string> currentDescription,
        SqlSchemaDetails details,
        DateTime exportedAt)
    {
        if (objects is null) throw new ArgumentNullException(nameof(objects));
        if (currentDescription is null) throw new ArgumentNullException(nameof(currentDescription));
        if (details is null) throw new ArgumentNullException(nameof(details));

        var tables = new List<SqlSchemaTable>();
        foreach (var item in objects)
        {
            var columns = new List<SqlSchemaColumn>(item.Columns.Count);
            for (var i = 0; i < item.Columns.Count; i++)
            {
                var column = item.Columns[i];
                columns.Add(new SqlSchemaColumn(
                    i + 1,
                    column,
                    currentDescription(item.TargetOf(column), column.Description),
                    details.DefaultOf(item.ObjectId, column.ColumnId),
                    details.ComputedOf(item.ObjectId, column.ColumnId),
                    details.ReferenceOf(item.ObjectId, column.ColumnId)));
            }

            tables.Add(new SqlSchemaTable(item, currentDescription(item.Target, item.Description), details.RowsOf(item.ObjectId), columns));
        }

        return new SqlSchemaDocument(serverName ?? string.Empty, databaseName ?? string.Empty, exportedAt, tables);
    }

    /// <summary>副檔名（含點）。</summary>
    public static string ExtensionOf(SqlSchemaExportFormat format) => format == SqlSchemaExportFormat.Markdown ? ".md" : ".xlsx";

    /// <summary>
    /// 照格式寫進 <paramref name="output"/>；每寫完一張表呼叫一次 <paramref name="tableWritten"/>（從 1 數）。
    /// </summary>
    public void Write(SqlSchemaExportFormat format, Stream output, Action<int>? tableWritten, CancellationToken cancellationToken)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));

        if (format == SqlSchemaExportFormat.Markdown)
        {
            using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n" };
            SqlSchemaMarkdown.Write(this, writer, tableWritten, cancellationToken);
            return;
        }

        SqlSchemaWorkbook.Build(this).Save(output, sheet =>
        {
            // 第一頁是總覽，資料表從第二頁開始。
            if (sheet > 1) tableWritten?.Invoke(sheet - 1);
        }, cancellationToken);
    }
}
