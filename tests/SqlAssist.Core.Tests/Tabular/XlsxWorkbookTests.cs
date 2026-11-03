using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using SqlAssist.Core.Tabular;
using Xunit;

namespace SqlAssist.Core.Tests.Tabular;

public sealed class XlsxWorkbookTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>
    /// 一份打得開的活頁簿要有的部件都在，每一頁一個工作表部件；頁寫完一頁回報一次。
    /// </summary>
    [Fact]
    public void 寫出必要部件且每頁回報一次()
    {
        var workbook = new XlsxWorkbook();
        workbook.AddSheet("總覽").SetText(1, 1, "Library");
        workbook.AddSheet("Loan").SetNumber(1, 1, 42);

        var written = new List<int>();
        var parts = Save(workbook, written.Add);

        Assert.Equal(new[] { 1, 2 }, written);
        Assert.Contains("[Content_Types].xml", parts.Keys);
        Assert.Contains("_rels/.rels", parts.Keys);
        Assert.Contains("xl/workbook.xml", parts.Keys);
        Assert.Contains("xl/_rels/workbook.xml.rels", parts.Keys);
        Assert.Contains("xl/styles.xml", parts.Keys);
        Assert.Contains("xl/worksheets/sheet1.xml", parts.Keys);
        Assert.Contains("xl/worksheets/sheet2.xml", parts.Keys);

        var sheets = parts["xl/workbook.xml"].Descendants(Main + "sheet").Select(sheet => (string)sheet.Attribute("name")!).ToArray();
        Assert.Equal(new[] { "總覽", "Loan" }, sheets);
    }

    /// <summary>
    /// 文字寫成行內字串並保留空白，數字寫成值；凍結、自動篩選與頁內連結照結構描述的順序寫在工作表裡，
    /// 篩選範圍另在活頁簿留一個隱藏名稱。
    /// </summary>
    [Fact]
    public void 儲存格凍結篩選與連結()
    {
        var workbook = new XlsxWorkbook();
        var overview = workbook.AddSheet("總覽");
        var loan = workbook.AddSheet("Loan's");
        overview.SetText(4, 1, "  讀者編號 ");
        overview.SetNumber(4, 2, 1234567890123);
        overview.SetLink(5, 1, "dbo.Loan", loan);
        overview.SetText(6, 1, null);
        overview.FrozenRows = 4;
        overview.SetFilter(4, 1, 5, 2);

        var parts = Save(workbook, null);
        var sheet = parts["xl/worksheets/sheet1.xml"].Root!;

        Assert.Equal(new[] { "sheetViews", "sheetData", "autoFilter", "hyperlinks" },
            sheet.Elements().Select(element => element.Name.LocalName).ToArray());

        var pane = sheet.Descendants(Main + "pane").Single();
        Assert.Equal("4", (string)pane.Attribute("ySplit")!);
        Assert.Equal("A5", (string)pane.Attribute("topLeftCell")!);
        Assert.Equal("frozen", (string)pane.Attribute("state")!);

        var cells = sheet.Descendants(Main + "c").ToDictionary(cell => (string)cell.Attribute("r")!);
        Assert.Equal("inlineStr", (string)cells["A4"].Attribute("t")!);
        Assert.Equal("  讀者編號 ", cells["A4"].Descendants(Main + "t").Single().Value);
        Assert.Equal("1234567890123", cells["B4"].Element(Main + "v")!.Value);
        Assert.False(cells.ContainsKey("A6"));

        Assert.Equal("A4:B5", (string)sheet.Element(Main + "autoFilter")!.Attribute("ref")!);
        var link = sheet.Descendants(Main + "hyperlink").Single();
        Assert.Equal("A5", (string)link.Attribute("ref")!);
        Assert.Equal("'Loan''s'!A1", (string)link.Attribute("location")!);

        var name = parts["xl/workbook.xml"].Descendants(Main + "definedName").Single();
        Assert.Equal("_xlnm._FilterDatabase", (string)name.Attribute("name")!);
        Assert.Equal("0", (string)name.Attribute("localSheetId")!);
        Assert.Equal("'總覽'!$A$4:$B$5", name.Value);
    }

    /// <summary>頁名照 Excel 的規則：去掉不收的字元、31 個字元以內、不叫 History、不分大小寫撞名加序號。</summary>
    [Fact]
    public void 頁名照Excel規則改寫並去重()
    {
        var workbook = new XlsxWorkbook();
        var names = new[]
        {
            workbook.AddSheet("dbo.Loan").Name,
            workbook.AddSheet("DBO.LOAN").Name,
            workbook.AddSheet("rpt/Loan[2026]:*?").Name,
            workbook.AddSheet("history").Name,
            workbook.AddSheet("").Name,
            workbook.AddSheet(new string('x', 40)).Name,
            workbook.AddSheet(new string('x', 40)).Name,
        };

        Assert.Equal("dbo.Loan", names[0]);
        Assert.Equal("DBO.LOAN (2)", names[1]);
        Assert.Equal("rpt_Loan_2026____", names[2]);
        Assert.Equal("Sheet", names[3]);
        Assert.Equal("Sheet (2)", names[4]);
        Assert.Equal(new string('x', 31), names[5]);
        Assert.Equal(new string('x', 27) + " (2)", names[6]);
    }

    /// <summary>使用者寫的說明混進 XML 不收的控制字元時拿掉，整份檔案才不會被 Excel 說毀損。</summary>
    [Fact]
    public void 控制字元拿掉且超長截斷()
    {
        Assert.Equal("借閱\t日期\n", XlsxCell.Clean("借\u0001閱\t日期\n\u000B"));
        Assert.Equal("😀", XlsxCell.Clean("😀"));
        Assert.Equal(32767, XlsxCell.Clean(new string('a', 40000)).Length);

        var workbook = new XlsxWorkbook();
        workbook.AddSheet("Loan").SetText(1, 1, "a\u0002b");
        var parts = Save(workbook, null);
        Assert.Equal("ab", parts["xl/worksheets/sheet1.xml"].Descendants(Main + "t").Single().Value);
    }

    /// <summary>
    /// 文件版面：關掉格線、頁籤上色、列高、開檔游標與橫向一頁寬的列印；第 0 個格式不帶框線——
    /// Excel 拿它畫整頁沒寫到的格子；沒有 12 以下的字級。
    /// </summary>
    [Fact]
    public void 文件版面()
    {
        var workbook = new XlsxWorkbook();
        var sheet = workbook.AddSheet("總覽");
        sheet.ShowGridLines = false;
        sheet.TabColor = "FF0066CC";
        sheet.PrintLandscapeFitWidth = true;
        sheet.FrozenRows = 4;
        sheet.SelectedCell = (5, 2);
        sheet.SetHeight(1, 34);
        sheet.SetHeight(3, 8);
        sheet.SetText(1, 1, "Library", XlsxStyle.Title);
        sheet.SetText(5, 2, null);
        sheet.SetText(5, 3, "讀者");

        var parts = Save(workbook, null);
        var root = parts["xl/worksheets/sheet1.xml"].Root!;

        Assert.Equal(new[] { "sheetPr", "sheetViews", "sheetData", "pageMargins", "pageSetup" },
            root.Elements().Select(element => element.Name.LocalName).ToArray());
        Assert.Equal("FF0066CC", (string)root.Descendants(Main + "tabColor").Single().Attribute("rgb")!);
        var view = root.Descendants(Main + "sheetView").Single();
        Assert.Equal("0", (string)view.Attribute("showGridLines")!);
        var selection = view.Element(Main + "selection")!;
        Assert.Equal("bottomLeft", (string)selection.Attribute("pane")!);
        Assert.Equal("B5", (string)selection.Attribute("activeCell")!);
        Assert.Equal("landscape", (string)root.Element(Main + "pageSetup")!.Attribute("orientation")!);

        var rows = root.Descendants(Main + "row").ToDictionary(row => (string)row.Attribute("r")!);
        Assert.Equal("34", (string)rows["1"].Attribute("ht")!);
        Assert.Empty(rows["3"].Elements());
        Assert.Equal(new[] { "C5" }, rows["5"].Elements().Select(cell => (string)cell.Attribute("r")!).ToArray());

        var styles = parts["xl/styles.xml"];
        var formats = styles.Descendants(Main + "cellXfs").Single().Elements().ToArray();
        Assert.Equal(Enum.GetValues(typeof(XlsxStyle)).Length + 1, formats.Length);
        Assert.Equal("0", (string)formats[0].Attribute("borderId")!);
        Assert.Equal("0", (string)formats[(int)XlsxStyle.Text + 1].Attribute("borderId")!);
        Assert.NotEqual("0", (string)formats[(int)XlsxStyle.HeaderCenter + 1].Attribute("borderId")!);

        var sizes = styles.Descendants(Main + "sz").Select(size => double.Parse((string)size.Attribute("val")!)).ToArray();
        Assert.All(sizes, size => Assert.True(size >= 12));
    }

    /// <summary>
    /// 隔列淡底是條件式格式：範圍從表身第一列到最後一列，公式數第一欄看得見的列（篩選後仍交替），
    /// 寫在自動篩選與連結之間；一列都沒有時不寫。
    /// </summary>
    [Fact]
    public void 隔列淡底()
    {
        var workbook = new XlsxWorkbook();
        var sheet = workbook.AddSheet("Loan");
        var other = workbook.AddSheet("Empty");
        sheet.SetLink(1, 1, "← 總覽", other, XlsxStyle.Navigation);
        sheet.SetFilter(4, 1, 9, 3);
        sheet.SetBanding(5, 9, 3);
        other.SetBanding(5, 4, 3);

        var parts = Save(workbook, null);
        var root = parts["xl/worksheets/sheet1.xml"].Root!;
        Assert.Equal(new[] { "sheetViews", "sheetData", "autoFilter", "conditionalFormatting", "hyperlinks" },
            root.Elements().Select(element => element.Name.LocalName).ToArray());

        var band = root.Element(Main + "conditionalFormatting")!;
        Assert.Equal("A5:C9", (string)band.Attribute("sqref")!);
        Assert.Equal("MOD(SUBTOTAL(3,$A$5:$A5),2)=0", band.Descendants(Main + "formula").Single().Value);
        Assert.Equal("0", (string)band.Element(Main + "cfRule")!.Attribute("dxfId")!);
        Assert.Single(parts["xl/styles.xml"].Descendants(Main + "dxf"));

        Assert.Empty(parts["xl/worksheets/sheet2.xml"].Descendants(Main + "conditionalFormatting"));
    }

    /// <summary>
    /// 欄寬照表格裡的內容估（全形字算兩格，表頭多算篩選鈕），標題這種往右延伸的不算；夾在上下限之間。
    /// 列高照換行的格子估行數，換行字元另起一行。
    /// </summary>
    [Fact]
    public void 欄寬與列高照內容估()
    {
        var workbook = new XlsxWorkbook();
        var sheet = workbook.AddSheet("Loan");
        sheet.SetText(1, 2, "一個很長很長很長很長很長很長很長很長很長的標題", XlsxStyle.Title);
        sheet.SetText(4, 2, "NULL", XlsxStyle.HeaderCenter);
        sheet.SetText(4, 3, "描述", XlsxStyle.Header);
        sheet.SetText(5, 2, "✓", XlsxStyle.Center);
        sheet.SetText(5, 3, "讀者編號\n見讀者主檔");
        sheet.SetText(6, 3, new string('借', 40));
        sheet.SetFilter(4, 1, 6, 3);
        sheet.FitWidth(2, 6, 30);
        sheet.FitWidth(3, 10, 20);
        sheet.FitWidth(4, 7, 16);
        sheet.FitHeights(5, 6, 16.5, 9);

        var root = Save(workbook, null)["xl/worksheets/sheet1.xml"].Root!;
        var widths = root.Descendants(Main + "col").ToDictionary(col => (string)col.Attribute("min")!, col => (string)col.Attribute("width")!);
        Assert.Equal("8", widths["2"]);
        Assert.Equal("20", widths["3"]);
        Assert.Equal("7", widths["4"]);

        var heights = root.Descendants(Main + "row").ToDictionary(row => (string)row.Attribute("r")!, row => (string?)row.Attribute("ht"));
        Assert.Equal("42", heights["5"]);
        Assert.Equal("91.5", heights["6"]);

        Assert.Equal(8, XlsxCell.DisplayWidth("讀者編號\nab"));
        Assert.Equal(2, XlsxCell.DisplayWidth("😀"));
        Assert.Equal(3, XlsxCell.LineCount("abcdefghij\n", 5));
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(702, "ZZ")]
    [InlineData(703, "AAA")]
    public void 欄名(int column, string name) => Assert.Equal(name, XlsxCell.ColumnName(column));

    [Fact]
    public void 停止時不再往下寫()
    {
        var workbook = new XlsxWorkbook();
        workbook.AddSheet("A");
        workbook.AddSheet("B");
        using var cancel = new CancellationTokenSource();
        using var output = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() => workbook.Save(output, _ => cancel.Cancel(), cancel.Token));
    }

    private static Dictionary<string, XDocument> Save(XlsxWorkbook workbook, Action<int>? sheetWritten)
    {
        using var output = new MemoryStream();
        workbook.Save(output, sheetWritten, CancellationToken.None);
        output.Position = 0;

        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        });
    }
}
