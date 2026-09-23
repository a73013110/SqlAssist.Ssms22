using System;
using System.Linq;
using System.Text;
using SqlAssist.Core.Tabular;
using Xunit;

namespace SqlAssist.Core.Tests.Tabular;

public sealed class SqlTabularTextTests
{
    private sealed record Row(string? Name, string? Note);

    private static readonly SqlTabularColumn<Row>[] Columns =
    {
        new("名稱", row => row.Name),
        new("說明", row => row.Note),
    };

    [Fact]
    public void 標頭列在前且列順序照輸入()
    {
        var tsv = SqlTabularText.ToTsv(Columns, new[] { new Row("Loan", "借閱"), new Row("Branch", "分館") });

        Assert.Equal("名稱\t說明\r\nLoan\t借閱\r\nBranch\t分館\r\n", tsv);
    }

    [Fact]
    public void 定位字元換行與引號照Excel規則加引號()
    {
        var tsv = SqlTabularText.ToTsv(Columns, new[]
        {
            new Row("Lib\tReader", "第一行\r\n第二行"),
            new Row("\"Cat_BookCopy\"", "LF\n與 CR\r"),
        });

        Assert.Equal(
            "名稱\t說明\r\n" +
            "\"Lib\tReader\"\t\"第一行\r\n第二行\"\r\n" +
            "\"\"\"Cat_BookCopy\"\"\"\t\"LF\n與 CR\r\"\r\n",
            tsv);
    }

    [Theory]
    [InlineData("Loan")]
    [InlineData("a\tb")]
    [InlineData("say \"hi\"")]
    [InlineData("x\ny")]
    [InlineData("\"")]
    [InlineData("")]
    public void 儲存格規則與資料格匯出原本那一份相同(string value)
    {
        // SqlDataGridText 原本的寫法：含特殊字元就整格包引號並把引號重複一次。
        var expected = value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

        var tsv = SqlTabularText.ToTsv(new[] { new SqlTabularColumn<string>("h", text => text) }, new[] { value });

        Assert.Equal("h\r\n" + expected + "\r\n", tsv);
    }

    [Fact]
    public void 空值是空格不是null字樣()
    {
        var tsv = SqlTabularText.ToTsv(Columns, new[] { new Row(null, null) });

        Assert.Equal("名稱\t說明\r\n\t\r\n", tsv);
    }

    [Fact]
    public void 沒有資料列時只有標頭()
    {
        var content = SqlTabularText.Build(Columns, Array.Empty<Row>());

        Assert.Equal("名稱\t說明\r\n", content.Tsv);
        Assert.Equal(0, content.RowCount);
        Assert.Contains("<th>名稱</th>", content.Html);
    }

    [Fact]
    public void Unicode原樣保留()
    {
        var content = SqlTabularText.Build(Columns, new[] { new Row("讀者😀", "é") });

        Assert.Contains("讀者😀\té", content.Tsv);
        Assert.Contains("<td>讀者😀</td><td>é</td>", Fragment(content.Html));
    }

    [Fact]
    public void Html特殊字元編碼且格內換行留在同一格()
    {
        var content = SqlTabularText.Build(Columns, new[] { new Row("<Loan & \"Copy\">", "a\r\nb\nc") });

        var fragment = Fragment(content.Html);
        Assert.Contains("<td>&lt;Loan &amp; &quot;Copy&quot;&gt;</td>", fragment);
        Assert.Contains("<td>a<br style=\"mso-data-placement:same-cell\">b<br style=\"mso-data-placement:same-cell\">c</td>", fragment);
        Assert.StartsWith("<table><tr><th>名稱</th><th>說明</th></tr>", fragment);
        Assert.EndsWith("</table>", fragment);
    }

    [Fact]
    public void CfHtml位移以UTF8位元組計算()
    {
        var content = SqlTabularText.Build(Columns, new[] { new Row("讀者😀", "借閱"), new Row("é", "\uD800落單") });
        var html = content.Html;
        var bytes = Encoding.UTF8.GetBytes(html);

        var startHtml = Offset(html, "StartHTML");
        var endHtml = Offset(html, "EndHTML");
        var startFragment = Offset(html, "StartFragment");
        var endFragment = Offset(html, "EndFragment");

        Assert.Equal(bytes.Length, endHtml);
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));
        Assert.EndsWith("<!--StartFragment-->", Encoding.UTF8.GetString(bytes, 0, startFragment));
        Assert.StartsWith("<!--EndFragment-->", Encoding.UTF8.GetString(bytes, endFragment, bytes.Length - endFragment));
        var fragment = Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment);
        Assert.StartsWith("<table>", fragment);
        Assert.EndsWith("</table>", fragment);
        // 落單的代理字元先換成 U+FFFD，位移才與剪貼簿實際收到的位元組對得上。
        Assert.Contains("�落單", fragment);
    }

    [Fact]
    public void 上千列不會因為串接而變慢()
    {
        var rows = Enumerable.Range(0, 5000).Select(index => new Row("Loan" + index, "借閱\t" + index)).ToArray();

        var content = SqlTabularText.Build(Columns, rows);

        Assert.Equal(5000, content.RowCount);
        Assert.Equal(5001, content.Tsv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
        // 每一格的值只取一次：兩種格式同一趟寫完。
        var calls = 0;
        SqlTabularText.Build(new[] { new SqlTabularColumn<Row>("n", row => { calls++; return row.Name; }) }, rows);
        Assert.Equal(rows.Length, calls);
    }

    [Fact]
    public void 沒有欄位是呼叫端的錯()
    {
        Assert.Throws<ArgumentException>(() => SqlTabularText.ToTsv(Array.Empty<SqlTabularColumn<Row>>(), Array.Empty<Row>()));
    }

    private static int Offset(string html, string label)
    {
        var start = html.IndexOf(label + ":", StringComparison.Ordinal) + label.Length + 1;
        return int.Parse(html.Substring(start, 10), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Fragment(string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        var start = Offset(html, "StartFragment");
        return Encoding.UTF8.GetString(bytes, start, Offset(html, "EndFragment") - start);
    }
}
