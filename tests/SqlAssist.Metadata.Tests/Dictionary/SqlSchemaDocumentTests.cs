using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

public sealed class SqlSchemaDocumentTests
{
    private static readonly DateTime ExportedAt = new(2026, 11, 3, 9, 30, 0);

    /// <summary>
    /// 四段結果收成細節：沒有運算式（沒權限讀定義）的那一筆不收、一個資料行參照兩處時串起來、
    /// 資料列數是 NULL 時算 0。
    /// </summary>
    [Fact]
    public void 細節四段結果()
    {
        using var reader = Details().CreateDataReader();
        var details = SqlSchemaDetails.Read(reader, CancellationToken.None);

        Assert.Equal("(getdate())", details.DefaultOf(2, 3));
        Assert.Null(details.DefaultOf(2, 2));
        Assert.Equal("([LoanNo]*(10))", details.ComputedOf(3, 2));
        Assert.Equal("dbo.Lib_Reader.ReaderNo, arc.Lib_Reader.ReaderNo", details.ReferenceOf(2, 2));
        Assert.Equal(1200, details.RowsOf(1));
        Assert.Equal(0, details.RowsOf(3));
        Assert.Null(details.RowsOf(4));
    }

    /// <summary>
    /// 文件照呼叫端給的順序收物件；說明取畫面上的那一份（含還沒套用的編輯），
    /// 鍵、識別、計算式與參照從字典與細節併起來，順序從 1 連續數。
    /// </summary>
    [Fact]
    public void 文件併起字典與細節()
    {
        var document = Build(LibraryDictionary.Create().Objects.Reverse().Take(3).Reverse());

        Assert.Equal(new[] { "dbo.Loan", "dbo.LoanDetail", "rpt.LoanByReader" },
            document.Tables.Select(table => table.Object.DisplayName).ToArray());

        var loan = document.Tables[0];
        Assert.Equal("借閱主檔", loan.Description);
        Assert.Equal(2, loan.Described);
        Assert.Equal(500, loan.Rows);
        Assert.Equal(new[] { "PK", "FK", "" }, loan.Columns.Select(column => column.Key).ToArray());
        Assert.Equal("(getdate())", loan.Columns[2].Default);
        Assert.Equal("借閱者|讀者編號\n見讀者主檔", loan.Columns[1].Description);
        Assert.NotEmpty(loan.Columns[0].Traits);

        var view = document.Tables[2];
        Assert.Null(view.Rows);
        Assert.Equal(new[] { 1, 2 }, view.Columns.Select(column => column.Ordinal).ToArray());
    }

    /// <summary>
    /// Markdown：目錄連到 GitHub 規則推出的錨點，儲存格裡的直線跳脫、換行換成 &lt;br&gt;，
    /// 型別、預設值與參照用行內程式碼；UTF-8 無 BOM、LF。
    /// </summary>
    [Fact]
    public void Markdown版面()
    {
        var document = Build(LibraryDictionary.Create().Objects.Take(2));
        var written = new List<int>();

        using var output = new MemoryStream();
        document.Write(SqlSchemaExportFormat.Markdown, output, written.Add, CancellationToken.None);
        var bytes = output.ToArray();
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal(new[] { 1, 2 }, written);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain("\r", text);
        Assert.StartsWith("# Library ", text);
        Assert.Contains("[dbo.Lib_Reader](#dbolib_reader)", text);
        Assert.Contains("\n## dbo.Loan\n", text);
        Assert.Contains("| 2 | ReaderNo | `varchar(10)` |  | FK | ", text);
        Assert.Contains("`dbo.Lib_Reader.ReaderNo, arc.Lib_Reader.ReaderNo`", text);
        Assert.Contains("借閱者\\|讀者編號<br>見讀者主檔", text);
        Assert.Contains("`(getdate())`", text);
    }

    /// <summary>
    /// Excel：第一頁是總覽，每張表一頁；頁名用資料表名稱，不同結構描述同名時才帶結構描述。
    /// 寫完一張表回報一次（總覽不算）。
    /// </summary>
    [Fact]
    public void 活頁簿每張表一頁()
    {
        var objects = LibraryDictionary.Create().Objects.ToList();
        objects.Add(new SqlDictionaryObject(9, "arc", "Lib_Reader", SqlDictionaryObjectKind.Table, null, new[]
        {
            new SqlDictionaryColumn(1, "ReaderNo", "varchar(10)", isNullable: false),
        }));
        var document = Build(objects);

        var workbook = SqlSchemaWorkbook.Build(document);
        Assert.Equal(new[] { "dbo.Lib_Reader", "Loan", "LoanDetail", "LoanByReader", "arc.Lib_Reader" },
            workbook.Sheets.Skip(1).Select(sheet => sheet.Name).ToArray());

        var written = new List<int>();
        using var output = new MemoryStream();
        document.Write(SqlSchemaExportFormat.Excel, output, written.Add, CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, written);

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(6, archive.Entries.Count(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(SqlSchemaExportFormat.Excel, ".xlsx")]
    [InlineData(SqlSchemaExportFormat.Markdown, ".md")]
    public void 副檔名(SqlSchemaExportFormat format, string extension) =>
        Assert.Equal(extension, SqlSchemaDocument.ExtensionOf(format));

    private static SqlSchemaDocument Build(IEnumerable<SqlDictionaryObject> objects)
    {
        using var reader = Details().CreateDataReader();
        var details = SqlSchemaDetails.Read(reader, CancellationToken.None);
        var edits = new Dictionary<string, string>
        {
            ["dbo.Loan"] = "借閱主檔",
            ["dbo.Loan.ReaderNo"] = "借閱者|讀者編號\n見讀者主檔",
        };

        return SqlSchemaDocument.Build("LIBSQL01", "Library", objects,
            (target, original) => edits.TryGetValue(Key(target), out var value) ? value : original,
            details, ExportedAt);

        static string Key(SqlDescriptionTarget target) =>
            target.SchemaName + "." + target.ObjectName + (target.ColumnName is { } column ? "." + column : "");
    }

    /// <summary>圖書館字典的物件編號：1 讀者、2 借閱、3 借閱明細、4 檢視。</summary>
    private static DataSet Details()
    {
        var set = new DataSet();
        var defaults = set.Tables.Add("defaults");
        defaults.Columns.Add("parent_object_id", typeof(int));
        defaults.Columns.Add("parent_column_id", typeof(int));
        defaults.Columns.Add("definition", typeof(string));
        defaults.Rows.Add(2, 3, "(getdate())");
        defaults.Rows.Add(2, 2, DBNull.Value);

        var computed = set.Tables.Add("computed");
        computed.Columns.Add("object_id", typeof(int));
        computed.Columns.Add("column_id", typeof(int));
        computed.Columns.Add("definition", typeof(string));
        computed.Rows.Add(3, 2, "([LoanNo]*(10))");

        var references = set.Tables.Add("references");
        references.Columns.Add("parent_object_id", typeof(int));
        references.Columns.Add("parent_column_id", typeof(int));
        references.Columns.Add("schema", typeof(string));
        references.Columns.Add("table", typeof(string));
        references.Columns.Add("column", typeof(string));
        references.Rows.Add(2, 2, "dbo", "Lib_Reader", "ReaderNo");
        references.Rows.Add(2, 2, "arc", "Lib_Reader", "ReaderNo");

        var rows = set.Tables.Add("rows");
        rows.Columns.Add("object_id", typeof(int));
        rows.Columns.Add("rows", typeof(long));
        rows.Rows.Add(1, 1200L);
        rows.Rows.Add(2, 500L);
        rows.Rows.Add(3, DBNull.Value);
        return set;
    }
}
