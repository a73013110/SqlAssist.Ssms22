using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace SqlAssist.Core.Tabular;

/// <summary>
/// 儲存格的外觀；一份活頁簿只有這幾種，樣式表照這個順序寫。
/// </summary>
/// <remarks>
/// 表身的幾種（文字、連結、數字、置中、等寬、序號）不畫框線：列與列靠 <see cref="XlsxSheet.SetBanding"/>
/// 隔列的淡底分開，框線只剩表頭下緣那一條。表身以外的（標題、脈絡、說明、導覽）不算進欄寬與列高的估算。
/// </remarks>
public enum XlsxStyle
{
    /// <summary>表身的一般文字：垂直置中、換行、左邊內縮一格。</summary>
    Text,

    /// <summary>每一頁最上面那一行：半粗、大字。</summary>
    Title,

    /// <summary>淡色的一行脈絡（範圍、筆數、時間）；不換行，長了往右邊的空格延伸。</summary>
    Dim,

    /// <summary>表頭：半粗、下緣一條深色細線，靠左內縮。</summary>
    Header,

    /// <summary>表身裡跳到另一頁的連結：連結色，不加底線。</summary>
    Link,

    /// <summary>整數，千分位，置中——表頭右側是篩選鈕，靠右的數字會與它擠在一起。</summary>
    Number,

    /// <summary>置中的記號（✓、鍵）。</summary>
    Center,

    /// <summary>等寬字：型別、預設值與計算式這些照抄 SQL 的內容。</summary>
    Code,

    /// <summary>不換行的一般文字：標題下那一行說明，長了往右邊的空格延伸，不被第一欄的欄寬折成一條。</summary>
    Plain,

    /// <summary>序號：最淡的灰、置中；是列的位置，不是資料，不該搶眼。</summary>
    Ordinal,

    /// <summary>表外的導覽連結（每一頁左上角的「← 總覽」）：連結色。</summary>
    Navigation,

    /// <summary>置中的表頭：放在序號、記號與數字這幾欄上，與底下的值對齊。</summary>
    HeaderCenter
}

/// <summary>各種外觀在估算欄寬與列高時的角色。</summary>
internal static class XlsxStyles
{
    /// <summary>表格裡的格子；標題、脈絡、說明與導覽往右延伸，不撐欄寬。</summary>
    public static bool InTable(XlsxStyle style) =>
        style is not (XlsxStyle.Title or XlsxStyle.Dim or XlsxStyle.Plain or XlsxStyle.Navigation);

    /// <summary>會換行的那幾種；列高照它們估。</summary>
    public static bool Wraps(XlsxStyle style) => style is XlsxStyle.Text or XlsxStyle.Code;

    /// <summary>左邊內縮一格的那幾種；估寬時多算那一格。</summary>
    public static bool Indents(XlsxStyle style) =>
        style is XlsxStyle.Text or XlsxStyle.Code or XlsxStyle.Header or XlsxStyle.Link;
}

/// <summary>
/// 最小的 Office Open XML 活頁簿寫入器：文字、整數、頁內連結、凍結表頭、自動篩選、隔列淡底、
/// 依內容估的欄寬與列高、格線、頁籤色、開檔游標與列印版面。
/// </summary>
/// <remarks>
/// 不拉第三方套件：要的只是「一張表一頁，表頭凍結可篩選」，整份格式寫起來是幾個固定的 XML 部件。
/// 字串一律寫成內嵌字串（<c>inlineStr</c>），不建共用字串表——匯出的是說明文字，重複的少，
/// 少一個部件就少一份要對齊的索引。
///
/// 一頁一頁寫進壓縮串流，寫完一頁回報一次進度；取消時丟 <see cref="OperationCanceledException"/>，
/// 呼叫端負責丟掉寫了一半的檔案。
/// </remarks>
public sealed class XlsxWorkbook
{
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly List<XlsxSheet> _sheets = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<XlsxSheet> Sheets => _sheets;

    /// <summary>加一頁；名稱照 Excel 的規則改寫並去重，回傳的那一頁帶著實際的名稱。</summary>
    public XlsxSheet AddSheet(string name)
    {
        var sheet = new XlsxSheet(XlsxSheetNames.MakeUnique(name, _names));
        _names.Add(sheet.Name);
        _sheets.Add(sheet);
        return sheet;
    }

    /// <summary>整份寫進 <paramref name="output"/>；每寫完一頁呼叫一次 <paramref name="sheetWritten"/>（從 1 數）。</summary>
    [Localizable(false)]
    public void Save(Stream output, Action<int>? sheetWritten, CancellationToken cancellationToken)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (_sheets.Count == 0) throw new InvalidOperationException("活頁簿至少要有一頁。");

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WritePart(archive, "[Content_Types].xml", WriteContentTypes);
        WritePart(archive, "_rels/.rels", WriteRootRelationships);
        WritePart(archive, "xl/workbook.xml", WriteWorkbook);
        WritePart(archive, "xl/_rels/workbook.xml.rels", WriteWorkbookRelationships);
        WritePart(archive, "xl/styles.xml", WriteStyles);

        for (var i = 0; i < _sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = _sheets[i];
            WritePart(archive, "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml", writer => sheet.Write(writer));
            sheetWritten?.Invoke(i + 1);
        }
    }

    private static void WritePart(ZipArchive archive, string path, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false });
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private void WriteContentTypes(XmlWriter writer)
    {
        writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
        Default(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default(writer, "xml", "application/xml");
        Override(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        Override(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        for (var i = 1; i <= _sheets.Count; i++)
        {
            Override(writer, "/xl/worksheets/sheet" + i.ToString(CultureInfo.InvariantCulture) + ".xml",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        }

        writer.WriteEndElement();

        static void Default(XmlWriter writer, string extension, string type)
        {
            writer.WriteStartElement("Default");
            writer.WriteAttributeString("Extension", extension);
            writer.WriteAttributeString("ContentType", type);
            writer.WriteEndElement();
        }

        static void Override(XmlWriter writer, string part, string type)
        {
            writer.WriteStartElement("Override");
            writer.WriteAttributeString("PartName", part);
            writer.WriteAttributeString("ContentType", type);
            writer.WriteEndElement();
        }
    }

    private static void WriteRootRelationships(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        Relationship(writer, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
        writer.WriteEndElement();
    }

    private void WriteWorkbookRelationships(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        for (var i = 1; i <= _sheets.Count; i++)
        {
            Relationship(writer, "rId" + i.ToString(CultureInfo.InvariantCulture),
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                "worksheets/sheet" + i.ToString(CultureInfo.InvariantCulture) + ".xml");
        }

        Relationship(writer, "rId" + (_sheets.Count + 1).ToString(CultureInfo.InvariantCulture),
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
        writer.WriteEndElement();
    }

    private static void Relationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    /// <summary>
    /// 活頁簿：頁的清單，以及每一頁自動篩選範圍的隱藏名稱。
    /// </summary>
    /// <remarks>
    /// <c>_xlnm._FilterDatabase</c> 不寫的話 Excel 照樣打得開，但另存或排序之後篩選按鈕會掉；
    /// 那是 Excel 自己存檔時一定會寫的那一個名稱。
    /// </remarks>
    private void WriteWorkbook(XmlWriter writer)
    {
        writer.WriteStartElement("workbook", MainNamespace);
        writer.WriteAttributeString("xmlns", "r", null, RelationshipNamespace);
        writer.WriteStartElement("sheets", MainNamespace);
        for (var i = 0; i < _sheets.Count; i++)
        {
            writer.WriteStartElement("sheet", MainNamespace);
            writer.WriteAttributeString("name", _sheets[i].Name);
            writer.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("id", RelationshipNamespace, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        var filtered = false;
        for (var i = 0; i < _sheets.Count; i++)
        {
            if (_sheets[i].FilterRange is not { } range) continue;
            if (!filtered)
            {
                writer.WriteStartElement("definedNames", MainNamespace);
                filtered = true;
            }

            writer.WriteStartElement("definedName", MainNamespace);
            writer.WriteAttributeString("name", "_xlnm._FilterDatabase");
            writer.WriteAttributeString("localSheetId", i.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("hidden", "1");
            writer.WriteString(XlsxSheetNames.Quote(_sheets[i].Name) + "!" + XlsxCell.Absolute(range));
            writer.WriteEndElement();
        }

        if (filtered) writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>內文字型；Windows 都有，中文由系統的字型連結補上微軟正黑體。</summary>
    private const string BodyFont = "Segoe UI";

    /// <summary>標題與表頭；同一個字族的半粗，比 Excel 的粗體細緻。</summary>
    private const string HeadingFont = "Segoe UI Semibold";

    private const string CodeFont = "Consolas";

    /// <summary>字：近黑而不是純黑。</summary>
    private const string Ink = "FF1D1D1F";

    /// <summary>脈絡那一行的中灰。</summary>
    private const string Secondary = "FF6E6E73";

    /// <summary>序號的淡灰；列的位置，不是資料。</summary>
    private const string Tertiary = "FFAEAEB2";

    private const string LinkBlue = "FF0066CC";

    private const string CodeInk = "FF3A3A3C";

    /// <summary>隔列淡底；與表頭的深色下緣一起，是整張表僅有的兩種分隔。</summary>
    private const string BandFill = "FFF5F5F7";

    /// <summary>
    /// 樣式表：字型、框線與格式各幾種；<c>cellXfs</c> 第 0 個是空白格的預設，之後照 <see cref="XlsxStyle"/> 的順序；
    /// 隔列淡底是 <c>dxfs</c> 的第 0 個，由條件式格式套上。
    /// </summary>
    /// <remarks>
    /// 字級：內文、表頭、脈絡與程式碼都是 12，標題 20——沒有 10、11，螢幕上讀描述不必放大。
    /// 色票只有一組中性灰加一個連結藍；框線只剩表頭下緣那一條，列與列靠隔列的淡底分開，
    /// 不畫列間線——淡底加橫線是兩種分隔疊在一起，讀起來比沒有更亂。
    /// </remarks>
    private static void WriteStyles(XmlWriter writer)
    {
        writer.WriteStartElement("styleSheet", MainNamespace);

        writer.WriteStartElement("numFmts");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("numFmt");
        writer.WriteAttributeString("numFmtId", "164");
        writer.WriteAttributeString("formatCode", "#,##0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        // 0 內文、1 標題、2 脈絡、3 表頭、4 連結、5 等寬、6 序號。
        writer.WriteStartElement("fonts");
        writer.WriteAttributeString("count", "7");
        Font(writer, 12, BodyFont, Ink);
        Font(writer, 20, HeadingFont, Ink);
        Font(writer, 12, BodyFont, Secondary);
        Font(writer, 12, HeadingFont, Ink);
        Font(writer, 12, BodyFont, LinkBlue);
        Font(writer, 12, CodeFont, CodeInk);
        Font(writer, 12, BodyFont, Tertiary);
        writer.WriteEndElement();

        // 0、1 是 Excel 保留的兩個；儲存格本身不填色，隔列淡底在 dxfs。
        writer.WriteStartElement("fills");
        writer.WriteAttributeString("count", "2");
        Fill(writer, "none", null);
        Fill(writer, "gray125", null);
        writer.WriteEndElement();

        // 0 無、1 表頭下緣。
        writer.WriteStartElement("borders");
        writer.WriteAttributeString("count", "2");
        writer.WriteStartElement("border");
        writer.WriteEndElement();
        BottomBorder(writer, Ink);
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", "0");
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs");
        writer.WriteAttributeString("count", (Enum.GetValues(typeof(XlsxStyle)).Length + 1).ToString(CultureInfo.InvariantCulture));
        // 第 0 個是沒寫到的格子用的：Excel 拿它畫整頁空白，帶了框線就是滿頁橫線。樣式從 1 起算。
        CellFormat(writer, font: 0, wrap: false);
        CellFormat(writer, font: 0, wrap: true, indent: true);                               // Text
        CellFormat(writer, font: 1, wrap: false);                                            // Title
        CellFormat(writer, font: 2, wrap: false);                                            // Dim
        CellFormat(writer, font: 3, wrap: false, border: 1, indent: true);                   // Header
        CellFormat(writer, font: 4, wrap: false, indent: true);                              // Link
        CellFormat(writer, font: 0, wrap: false, numberFormat: 164, horizontal: "center");   // Number
        CellFormat(writer, font: 0, wrap: false, horizontal: "center");                      // Center
        CellFormat(writer, font: 5, wrap: true, indent: true);                               // Code
        CellFormat(writer, font: 0, wrap: false);                                            // Plain
        CellFormat(writer, font: 6, wrap: false, horizontal: "center");                      // Ordinal
        CellFormat(writer, font: 4, wrap: false);                                            // Navigation
        CellFormat(writer, font: 3, wrap: false, border: 1, horizontal: "center");           // HeaderCenter
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyles");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle");
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        // 條件式格式的填色：實心填色在 dxf 裡讀的是 bgColor，fgColor 一起寫給其他讀取端。
        writer.WriteStartElement("dxfs");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("dxf");
        writer.WriteStartElement("fill");
        writer.WriteStartElement("patternFill");
        writer.WriteAttributeString("patternType", "solid");
        writer.WriteStartElement("fgColor");
        writer.WriteAttributeString("rgb", BandFill);
        writer.WriteEndElement();
        writer.WriteStartElement("bgColor");
        writer.WriteAttributeString("rgb", BandFill);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void BottomBorder(XmlWriter writer, string color)
    {
        writer.WriteStartElement("border");
        writer.WriteStartElement("bottom");
        writer.WriteAttributeString("style", "thin");
        writer.WriteStartElement("color");
        writer.WriteAttributeString("rgb", color);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Font(XmlWriter writer, double size, string name, string color)
    {
        writer.WriteStartElement("font");
        writer.WriteStartElement("sz");
        writer.WriteAttributeString("val", size.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteStartElement("color");
        writer.WriteAttributeString("rgb", color);
        writer.WriteEndElement();

        writer.WriteStartElement("name");
        writer.WriteAttributeString("val", name);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Fill(XmlWriter writer, string pattern, string? color)
    {
        writer.WriteStartElement("fill");
        writer.WriteStartElement("patternFill");
        writer.WriteAttributeString("patternType", pattern);
        if (color is not null)
        {
            writer.WriteStartElement("fgColor");
            writer.WriteAttributeString("rgb", color);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <param name="indent">左邊內縮一格：Excel 沒有儲存格內距，文字貼著框線讀起來擠。</param>
    private static void CellFormat(XmlWriter writer, int font, bool wrap, int fill = 0, int border = 0, int numberFormat = 0,
        string? horizontal = null, bool indent = false, string vertical = "center")
    {
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", numberFormat.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("borderId", border.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("xfId", "0");
        if (numberFormat != 0) writer.WriteAttributeString("applyNumberFormat", "1");
        if (font != 0) writer.WriteAttributeString("applyFont", "1");
        if (fill != 0) writer.WriteAttributeString("applyFill", "1");
        if (border != 0) writer.WriteAttributeString("applyBorder", "1");
        writer.WriteAttributeString("applyAlignment", "1");
        writer.WriteStartElement("alignment");
        if (indent)
        {
            writer.WriteAttributeString("horizontal", "left");
            writer.WriteAttributeString("indent", "1");
        }
        else if (horizontal is not null)
        {
            writer.WriteAttributeString("horizontal", horizontal);
        }

        writer.WriteAttributeString("vertical", vertical);
        if (wrap) writer.WriteAttributeString("wrapText", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}

/// <summary>活頁簿裡的一頁：儲存格、欄寬、凍結的表頭與自動篩選。</summary>
public sealed class XlsxSheet
{
    private readonly SortedDictionary<int, SortedDictionary<int, XlsxCell>> _rows = new();
    private readonly SortedDictionary<int, double> _widths = new();
    private readonly SortedDictionary<int, double> _heights = new();

    internal XlsxSheet(string name)
    {
        Name = name;
    }

    /// <summary>Excel 上看到的頁名；已經照規則改寫並去重。</summary>
    public string Name { get; }

    /// <summary>凍結在上面的列數；0 是不凍結。</summary>
    public int FrozenRows { get; set; }

    /// <summary>顯示格線；關掉時只剩樣式自己畫的框線。</summary>
    public bool ShowGridLines { get; set; } = true;

    /// <summary>頁籤的顏色（<c>AARRGGBB</c>）；null 是 Excel 的預設。</summary>
    public string? TabColor { get; set; }

    /// <summary>列印成橫向、寬度縮成一頁：欄位多的表直印時會被切成好幾頁。</summary>
    public bool PrintLandscapeFitWidth { get; set; }

    /// <summary>列高，以點為單位；沒設的列由 Excel 依內容決定（換行的說明自己長高）。</summary>
    public void SetHeight(int row, double points)
    {
        if (row < 1) throw new ArgumentOutOfRangeException(nameof(row));
        if (points <= 0) throw new ArgumentOutOfRangeException(nameof(points));
        _heights[row] = points;
    }

    /// <summary>自動篩選的範圍（列、欄都從 1 數）；null 是不篩選。</summary>
    public (int FirstRow, int FirstColumn, int LastRow, int LastColumn)? FilterRange { get; private set; }

    /// <summary>欄寬，以 Excel 的字元寬為單位；欄從 1 數。</summary>
    public void SetWidth(int column, double width)
    {
        if (column < 1) throw new ArgumentOutOfRangeException(nameof(column));
        _widths[column] = width;
    }

    /// <summary>寫一格文字；null 或空字串不寫。</summary>
    public void SetText(int row, int column, string? text, XlsxStyle style = XlsxStyle.Text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Put(row, column, XlsxCell.ForText(text!, style));
    }

    public void SetNumber(int row, int column, long value, XlsxStyle style = XlsxStyle.Number) =>
        Put(row, column, XlsxCell.ForNumber(value, style));

    /// <summary>寫一個跳到另一頁 A1 的連結。</summary>
    /// <param name="style">表身裡是 <see cref="XlsxStyle.Link"/>，表外的導覽是 <see cref="XlsxStyle.Navigation"/>。</param>
    public void SetLink(int row, int column, string text, XlsxSheet target, XlsxStyle style = XlsxStyle.Link)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        Put(row, column, XlsxCell.ForLink(text, XlsxSheetNames.Quote(target.Name) + "!A1", style));
    }

    public void SetFilter(int firstRow, int firstColumn, int lastRow, int lastColumn)
    {
        if (firstRow < 1 || firstColumn < 1 || lastRow < firstRow || lastColumn < firstColumn) throw new ArgumentOutOfRangeException(nameof(lastRow));
        FilterRange = (firstRow, firstColumn, lastRow, lastColumn);
    }

    /// <summary>隔列淡底的範圍（從第一欄起）；null 是不上底色。</summary>
    public (int FirstRow, int LastRow, int LastColumn)? Banding { get; private set; }

    /// <summary>
    /// 從 <paramref name="firstRow"/> 到 <paramref name="lastRow"/>，第 2、4、6…列上淡底；一列都沒有時不上。
    /// </summary>
    /// <remarks>
    /// 寫成條件式格式而不是每一格填色：填色跟著格子走，使用者一排序或篩選，條紋就亂了。
    /// 公式數的是第一欄到這一列為止<b>看得見</b>的非空格（<c>SUBTOTAL(3, …)</c>），篩掉的列不算，
    /// 所以篩選後仍是一列白一列灰——第一欄因此每一列都要有值（序號）。
    /// </remarks>
    public void SetBanding(int firstRow, int lastRow, int lastColumn)
    {
        if (firstRow < 1 || lastColumn < 1) throw new ArgumentOutOfRangeException(nameof(firstRow));
        Banding = lastRow < firstRow ? null : (firstRow, lastRow, lastColumn);
    }

    /// <summary>打開這一頁時游標停的那一格；null 是 A1。</summary>
    public (int Row, int Column)? SelectedCell { get; set; }

    /// <summary>
    /// 照這一欄表格裡的內容定欄寬，夾在 <paramref name="min"/> 與 <paramref name="max"/> 之間。
    /// </summary>
    /// <remarks>
    /// 空著的欄縮到表頭那麼寬，最右邊那一欄長描述才進得了第一個畫面。標題、脈絡與導覽往右延伸，不算。
    /// 估的是字元寬：全形字算兩格，內縮與篩選鈕另加；寧可寬一點，也不要把短值折成兩行。
    /// </remarks>
    public void FitWidth(int column, double min, double max)
    {
        var widest = 0d;
        foreach (var row in _rows)
        {
            if (!row.Value.TryGetValue(column, out var cell) || !XlsxStyles.InTable(cell.Style)) continue;
            var width = XlsxCell.DisplayWidth(cell.DisplayText) + CellPadding;
            if (XlsxStyles.Indents(cell.Style)) width += IndentWidth;
            if (FilterRange is { } range && range.FirstRow == row.Key) width += FilterButtonWidth;
            if (width > widest) widest = width;
        }

        SetWidth(column, Math.Min(max, Math.Max(min, widest)));
    }

    /// <summary>
    /// 從 <paramref name="firstRow"/> 到 <paramref name="lastRow"/>，照換行的格子估每一列的行數定列高。
    /// </summary>
    /// <remarks>
    /// Excel 自己長高的列只夠塞下字，一列一列貼在一起；每一列上下留白讀起來才鬆。
    /// 列高寫死之後 Excel 不再自己長，所以行數照欄寬估、寧多勿少，描述才不會被切掉一截。
    /// 欄寬要先定好（<see cref="FitWidth"/>）。
    /// </remarks>
    public void FitHeights(int firstRow, int lastRow, double lineHeight, double padding)
    {
        for (var row = firstRow; row <= lastRow; row++)
        {
            if (!_rows.TryGetValue(row, out var cells)) continue;
            var lines = 1;
            foreach (var cell in cells)
            {
                if (!XlsxStyles.Wraps(cell.Value.Style)) continue;
                var usable = (_widths.TryGetValue(cell.Key, out var width) ? width : DefaultWidth) - IndentWidth - CellPadding;
                lines = Math.Max(lines, XlsxCell.LineCount(cell.Value.Text, usable));
            }

            SetHeight(row, lines * lineHeight + padding);
        }
    }

    /// <summary>Excel 沒設欄寬時的寬。</summary>
    private const double DefaultWidth = 8.43;

    /// <summary>格子左右的留白（字元寬）。</summary>
    private const double CellPadding = 1.5;

    /// <summary>內縮一格的寬。</summary>
    private const double IndentWidth = 1.5;

    /// <summary>表頭右側篩選鈕佔的寬。</summary>
    private const double FilterButtonWidth = 2.5;

    private void Put(int row, int column, XlsxCell cell)
    {
        if (row < 1) throw new ArgumentOutOfRangeException(nameof(row));
        if (column < 1) throw new ArgumentOutOfRangeException(nameof(column));
        if (!_rows.TryGetValue(row, out var cells)) _rows[row] = cells = new SortedDictionary<int, XlsxCell>();
        cells[column] = cell;
    }

    /// <remarks>
    /// 部件裡的子元素順序是結構描述規定的：sheetPr → sheetViews → cols → sheetData → autoFilter
    /// → conditionalFormatting → hyperlinks → pageMargins → pageSetup。
    /// </remarks>
    internal void Write(XmlWriter writer)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        writer.WriteStartElement("worksheet", main);

        if (TabColor is not null || PrintLandscapeFitWidth)
        {
            writer.WriteStartElement("sheetPr", main);
            if (TabColor is not null)
            {
                writer.WriteStartElement("tabColor", main);
                writer.WriteAttributeString("rgb", TabColor);
                writer.WriteEndElement();
            }

            if (PrintLandscapeFitWidth)
            {
                writer.WriteStartElement("pageSetUpPr", main);
                writer.WriteAttributeString("fitToPage", "1");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteStartElement("sheetViews", main);
        writer.WriteStartElement("sheetView", main);
        if (!ShowGridLines) writer.WriteAttributeString("showGridLines", "0");
        writer.WriteAttributeString("workbookViewId", "0");
        if (FrozenRows > 0)
        {
            writer.WriteStartElement("pane", main);
            writer.WriteAttributeString("ySplit", FrozenRows.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("topLeftCell", XlsxCell.Reference(FrozenRows + 1, 1));
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
        }

        if (SelectedCell is { } selected)
        {
            var cell = XlsxCell.Reference(selected.Row, selected.Column);
            writer.WriteStartElement("selection", main);
            if (FrozenRows > 0) writer.WriteAttributeString("pane", "bottomLeft");
            writer.WriteAttributeString("activeCell", cell);
            writer.WriteAttributeString("sqref", cell);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        if (_widths.Count > 0)
        {
            writer.WriteStartElement("cols", main);
            foreach (var pair in _widths)
            {
                writer.WriteStartElement("col", main);
                var index = pair.Key.ToString(CultureInfo.InvariantCulture);
                writer.WriteAttributeString("min", index);
                writer.WriteAttributeString("max", index);
                writer.WriteAttributeString("width", pair.Value.ToString("0.##", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        var links = new List<(string Reference, string Location, string Display)>();
        writer.WriteStartElement("sheetData", main);
        var rows = new SortedSet<int>(_rows.Keys);
        rows.UnionWith(_heights.Keys);
        foreach (var index in rows)
        {
            writer.WriteStartElement("row", main);
            writer.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
            if (_heights.TryGetValue(index, out var height))
            {
                writer.WriteAttributeString("ht", height.ToString("0.##", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customHeight", "1");
            }

            if (_rows.TryGetValue(index, out var cells))
            {
                foreach (var cell in cells)
                {
                    var reference = XlsxCell.Reference(index, cell.Key);
                    cell.Value.Write(writer, reference);
                    if (cell.Value.Location is { } location) links.Add((reference, location, cell.Value.Text));
                }
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        if (FilterRange is { } range)
        {
            writer.WriteStartElement("autoFilter", main);
            writer.WriteAttributeString("ref", XlsxCell.Reference(range.FirstRow, range.FirstColumn) + ":" + XlsxCell.Reference(range.LastRow, range.LastColumn));
            writer.WriteEndElement();
        }

        if (Banding is { } band)
        {
            var first = band.FirstRow.ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement("conditionalFormatting", main);
            writer.WriteAttributeString("sqref", XlsxCell.Reference(band.FirstRow, 1) + ":" + XlsxCell.Reference(band.LastRow, band.LastColumn));
            writer.WriteStartElement("cfRule", main);
            writer.WriteAttributeString("type", "expression");
            writer.WriteAttributeString("dxfId", "0");
            writer.WriteAttributeString("priority", "1");
            writer.WriteElementString("formula", main, "MOD(SUBTOTAL(3,$A$" + first + ":$A" + first + "),2)=0");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        if (links.Count > 0)
        {
            writer.WriteStartElement("hyperlinks", main);
            foreach (var (reference, location, display) in links)
            {
                writer.WriteStartElement("hyperlink", main);
                writer.WriteAttributeString("ref", reference);
                writer.WriteAttributeString("location", location);
                writer.WriteAttributeString("display", XlsxCell.Clean(display));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        if (PrintLandscapeFitWidth)
        {
            writer.WriteStartElement("pageMargins", main);
            writer.WriteAttributeString("left", "0.5");
            writer.WriteAttributeString("right", "0.5");
            writer.WriteAttributeString("top", "0.6");
            writer.WriteAttributeString("bottom", "0.6");
            writer.WriteAttributeString("header", "0.3");
            writer.WriteAttributeString("footer", "0.3");
            writer.WriteEndElement();

            // 寬度一頁、高度不限：fitToHeight 寫 0，長表照樣往下分頁，不會被縮成一頁讀不到。
            writer.WriteStartElement("pageSetup", main);
            writer.WriteAttributeString("orientation", "landscape");
            writer.WriteAttributeString("fitToWidth", "1");
            writer.WriteAttributeString("fitToHeight", "0");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }
}

/// <summary>一格的值與外觀；寫出時才知道自己的位址。</summary>
internal sealed class XlsxCell
{
    private readonly long? _number;

    private XlsxCell(string text, long? number, XlsxStyle style, string? location)
    {
        Text = text;
        _number = number;
        Style = style;
        Location = location;
    }

    public string Text { get; }

    public XlsxStyle Style { get; }

    /// <summary>頁內連結的目的地（<c>'頁名'!A1</c>）；不是連結時為 null。</summary>
    public string? Location { get; }

    public static XlsxCell ForText(string text, XlsxStyle style) => new(text, null, style, null);

    public static XlsxCell ForNumber(long value, XlsxStyle style) => new("", value, style, null);

    public static XlsxCell ForLink(string text, string location, XlsxStyle style) => new(text, null, style, location);

    /// <summary>畫面上看到的字：數字照千分位寫。</summary>
    public string DisplayText => _number is { } number ? number.ToString("#,##0", CultureInfo.InvariantCulture) : Text;

    public void Write(XmlWriter writer, string reference)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        writer.WriteStartElement("c", main);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("s", ((int)Style + 1).ToString(CultureInfo.InvariantCulture));

        if (_number is { } number)
        {
            writer.WriteElementString("v", main, number.ToString(CultureInfo.InvariantCulture));
        }
        else if (Text.Length > 0)
        {
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", main);
            writer.WriteStartElement("t", main);
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(Clean(Text));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// 最長那一行佔幾個字元寬：全形字（中日韓、全形標點）與補充平面的字算 2，其他算 1。
    /// </summary>
    /// <remarks>
    /// 欄寬的單位是預設字型的數字寬；比例字型的拉丁字母多半比數字窄，全形字約是它的兩倍。
    /// 只拿來估欄寬與列高，寧寬勿窄。
    /// </remarks>
    public static double DisplayWidth(string text)
    {
        double widest = 0, current = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                widest = Math.Max(widest, current);
                current = 0;
                continue;
            }

            if (char.IsLowSurrogate(c)) continue;
            current += char.IsHighSurrogate(c) || IsWide(c) ? 2 : 1;
        }

        return Math.Max(widest, current);
    }

    /// <summary>在 <paramref name="width"/> 字元寬的欄裡，<paramref name="text"/> 折成幾行。</summary>
    public static int LineCount(string text, double width)
    {
        var usable = Math.Max(1, width);
        var lines = 0;
        foreach (var line in text.Split('\n'))
        {
            lines += Math.Max(1, (int)Math.Ceiling(DisplayWidth(line) / usable));
        }

        return lines;
    }

    private static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
        (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6);

    /// <summary>A1 樣式的位址；列、欄都從 1 數。</summary>
    public static string Reference(int row, int column) => ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>絕對位址的範圍（<c>$A$4:$J$20</c>），給隱藏名稱用。</summary>
    public static string Absolute((int FirstRow, int FirstColumn, int LastRow, int LastColumn) range) =>
        "$" + ColumnName(range.FirstColumn) + "$" + range.FirstRow.ToString(CultureInfo.InvariantCulture) + ":$" +
        ColumnName(range.LastColumn) + "$" + range.LastRow.ToString(CultureInfo.InvariantCulture);

    public static string ColumnName(int column)
    {
        var name = new StringBuilder();
        for (var value = column; value > 0; value = (value - 1) / 26)
        {
            name.Insert(0, (char)('A' + (value - 1) % 26));
        }

        return name.ToString();
    }

    /// <summary>
    /// 拿掉 XML 1.0 不收的控制字元；儲存格上限 32767 個字元，超過的截掉。
    /// </summary>
    /// <remarks>說明是使用者寫的任意文字，混進一個 <c>\u0001</c> 整份檔案 Excel 就說毀損。</remarks>
    public static string Clean(string text)
    {
        const int maxLength = 32767;
        StringBuilder? clean = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var valid = c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD) || char.IsSurrogate(c);
            if (valid)
            {
                clean?.Append(c);
                continue;
            }

            clean ??= new StringBuilder(text, 0, i, text.Length);
        }

        var result = clean?.ToString() ?? text;
        return result.Length > maxLength ? result.Substring(0, maxLength) : result;
    }
}

/// <summary>Excel 頁名的規則：31 個字元以內、不含 <c>[]:*?/\</c>、不分大小寫不重複、不能叫 History。</summary>
public static class XlsxSheetNames
{
    public const int MaxLength = 31;

    private static readonly char[] Invalid = { '[', ']', ':', '*', '?', '/', '\\' };

    /// <summary>照規則改寫 <paramref name="name"/>，與 <paramref name="taken"/> 撞名時在尾端加 <c> (2)</c>、<c> (3)</c>。</summary>
    public static string MakeUnique(string name, ISet<string> taken)
    {
        if (taken is null) throw new ArgumentNullException(nameof(taken));

        var clean = new StringBuilder();
        foreach (var c in name ?? string.Empty)
        {
            clean.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        var basis = clean.ToString().Trim().Trim('\'');
        if (basis.Length == 0 || string.Equals(basis, "History", StringComparison.OrdinalIgnoreCase)) basis = "Sheet";
        if (basis.Length > MaxLength) basis = basis.Substring(0, MaxLength);

        var candidate = basis;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            var suffix = " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
            candidate = (basis.Length + suffix.Length > MaxLength ? basis.Substring(0, MaxLength - suffix.Length) : basis) + suffix;
        }

        return candidate;
    }

    /// <summary>公式與連結裡引用頁名的寫法：一律加單引號，名稱裡的單引號寫兩次。</summary>
    public static string Quote(string name) => "'" + (name ?? string.Empty).Replace("'", "''") + "'";
}
