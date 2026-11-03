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

/// <summary>儲存格的外觀；一份活頁簿只有這幾種，樣式表照這個順序寫。</summary>
public enum XlsxStyle
{
    /// <summary>一般文字，靠上對齊並換行。</summary>
    Text,

    /// <summary>每一頁最上面那一行：粗體、大一級。</summary>
    Title,

    /// <summary>淡色的一行脈絡（範圍、筆數、時間）；不換行，長了往右邊的空格延伸。</summary>
    Dim,

    /// <summary>表頭：粗體、淡底、底線。</summary>
    Header,

    /// <summary>跳到另一頁的連結：連結色加底線。</summary>
    Link,

    /// <summary>整數，千分位。</summary>
    Number,

    /// <summary>置中的記號（✓）。</summary>
    Center,

    /// <summary>等寬字：型別、預設值與計算式這些照抄 SQL 的內容。</summary>
    Code,

    /// <summary>不換行的一般文字：標題下那一行說明，長了往右邊的空格延伸，不被第一欄的欄寬折成一條。</summary>
    Plain
}

/// <summary>
/// 最小的 Office Open XML 活頁簿寫入器：文字、整數、頁內連結、凍結表頭、自動篩選與欄寬。
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

    /// <summary>
    /// 樣式表：字型、填色與框線各幾種，<c>cellXfs</c> 的順序就是 <see cref="XlsxStyle"/> 的順序。
    /// </summary>
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

        // 0 一般、1 標題、2 淡色、3 粗體、4 連結、5 等寬。
        writer.WriteStartElement("fonts");
        writer.WriteAttributeString("count", "6");
        Font(writer, size: 11);
        Font(writer, size: 14, bold: true);
        Font(writer, size: 10, color: "FF6B7280");
        Font(writer, size: 11, bold: true);
        Font(writer, size: 11, color: "FF0563C1", underline: true);
        Font(writer, size: 10, name: "Consolas");
        writer.WriteEndElement();

        // 0、1 是 Excel 保留的兩個；2 是表頭的淡底。
        writer.WriteStartElement("fills");
        writer.WriteAttributeString("count", "3");
        Fill(writer, "none", null);
        Fill(writer, "gray125", null);
        Fill(writer, "solid", "FFF3F4F6");
        writer.WriteEndElement();

        writer.WriteStartElement("borders");
        writer.WriteAttributeString("count", "2");
        writer.WriteStartElement("border");
        writer.WriteEndElement();
        writer.WriteStartElement("border");
        writer.WriteStartElement("bottom");
        writer.WriteAttributeString("style", "thin");
        writer.WriteStartElement("color");
        writer.WriteAttributeString("rgb", "FFD0D7DE");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
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
        writer.WriteAttributeString("count", "9");
        CellFormat(writer, font: 0, wrap: true);                    // Text
        CellFormat(writer, font: 1, wrap: false);                   // Title
        CellFormat(writer, font: 2, wrap: false);                   // Dim
        CellFormat(writer, font: 3, wrap: false, fill: 2, border: 1); // Header
        CellFormat(writer, font: 4, wrap: false);                   // Link
        CellFormat(writer, font: 0, wrap: false, numberFormat: 164); // Number
        CellFormat(writer, font: 0, wrap: false, center: true);     // Center
        CellFormat(writer, font: 5, wrap: true);                    // Code
        CellFormat(writer, font: 0, wrap: false);                   // Plain
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyles");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle");
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void Font(XmlWriter writer, double size, bool bold = false, string? color = null, bool underline = false, string name = "Calibri")
    {
        writer.WriteStartElement("font");
        if (bold) writer.WriteElementString("b", null);
        if (underline) writer.WriteElementString("u", null);
        writer.WriteStartElement("sz");
        writer.WriteAttributeString("val", size.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        if (color is not null)
        {
            writer.WriteStartElement("color");
            writer.WriteAttributeString("rgb", color);
            writer.WriteEndElement();
        }

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

    private static void CellFormat(XmlWriter writer, int font, bool wrap, int fill = 0, int border = 0, int numberFormat = 0, bool center = false)
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
        writer.WriteAttributeString("vertical", "top");
        if (center) writer.WriteAttributeString("horizontal", "center");
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

    internal XlsxSheet(string name)
    {
        Name = name;
    }

    /// <summary>Excel 上看到的頁名；已經照規則改寫並去重。</summary>
    public string Name { get; }

    /// <summary>凍結在上面的列數；0 是不凍結。</summary>
    public int FrozenRows { get; set; }

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
    public void SetLink(int row, int column, string text, XlsxSheet target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        Put(row, column, XlsxCell.ForLink(text, XlsxSheetNames.Quote(target.Name) + "!A1"));
    }

    public void SetFilter(int firstRow, int firstColumn, int lastRow, int lastColumn)
    {
        if (firstRow < 1 || firstColumn < 1 || lastRow < firstRow || lastColumn < firstColumn) throw new ArgumentOutOfRangeException(nameof(lastRow));
        FilterRange = (firstRow, firstColumn, lastRow, lastColumn);
    }

    private void Put(int row, int column, XlsxCell cell)
    {
        if (row < 1) throw new ArgumentOutOfRangeException(nameof(row));
        if (column < 1) throw new ArgumentOutOfRangeException(nameof(column));
        if (!_rows.TryGetValue(row, out var cells)) _rows[row] = cells = new SortedDictionary<int, XlsxCell>();
        cells[column] = cell;
    }

    /// <remarks>部件裡的子元素順序是結構描述規定的：sheetViews → cols → sheetData → autoFilter → hyperlinks。</remarks>
    internal void Write(XmlWriter writer)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        writer.WriteStartElement("worksheet", main);

        writer.WriteStartElement("sheetViews", main);
        writer.WriteStartElement("sheetView", main);
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
        foreach (var row in _rows)
        {
            writer.WriteStartElement("row", main);
            writer.WriteAttributeString("r", row.Key.ToString(CultureInfo.InvariantCulture));
            foreach (var cell in row.Value)
            {
                var reference = XlsxCell.Reference(row.Key, cell.Key);
                cell.Value.Write(writer, reference);
                if (cell.Value.Location is { } location) links.Add((reference, location, cell.Value.Text));
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

    public static XlsxCell ForLink(string text, string location) => new(text, null, XlsxStyle.Link, location);

    public void Write(XmlWriter writer, string reference)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        writer.WriteStartElement("c", main);
        writer.WriteAttributeString("r", reference);
        if (Style != XlsxStyle.Text) writer.WriteAttributeString("s", ((int)Style).ToString(CultureInfo.InvariantCulture));

        if (_number is { } number)
        {
            writer.WriteElementString("v", main, number.ToString(CultureInfo.InvariantCulture));
        }
        else
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
