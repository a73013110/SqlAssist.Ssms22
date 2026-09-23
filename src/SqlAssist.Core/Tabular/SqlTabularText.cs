using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SqlAssist.Core.Tabular;

/// <summary>同一份表格的兩種剪貼簿格式；一次走完所有列產生。</summary>
public sealed class SqlTabularContent
{
    internal SqlTabularContent(string tsv, string html, int rowCount)
    {
        Tsv = tsv;
        Html = html;
        RowCount = rowCount;
    }

    /// <summary>Tab 分隔、CRLF 換行，第一列是標頭。</summary>
    public string Tsv { get; }

    /// <summary>CF_HTML（剪貼簿的「HTML Format」）：位移標頭加一張 <c>&lt;table&gt;</c>，位移以 UTF-8 位元組計。</summary>
    public string Html { get; }

    /// <summary>資料列數，不含標頭。</summary>
    public int RowCount { get; }
}

/// <summary>
/// 把一組欄位定義與一組列寫成 TSV 與 HTML 表格；資料格匯出與清單的批次複製共用這一份。
/// </summary>
/// <remarks>
/// 兩種格式同時放上剪貼簿，是因為貼的地方不同：記事本與查詢視窗讀 TSV，Excel、Word 與郵件讀
/// HTML。只放 TSV 的那一版在 Word 裡是一段夾著定位字元的文字。
///
/// 兩個 <see cref="StringBuilder"/> 同一趟寫完，每一格的值只取一次，也不先串出整列字串：
/// 上千列時逐格 <c>+</c> 串接是平方級的複製量。HTML 的位移在寫入時就逐字累計 UTF-8 位元組，
/// 最後回填到預留的十位數欄位裡，不必為了量長度再編碼一次整份內容。
/// </remarks>
public static class SqlTabularText
{
    /// <summary>列與列之間的換行；Excel 與剪貼簿的慣例是 CRLF。</summary>
    public const string NewLine = "\r\n";

    private static readonly char[] QuotedCharacters = { '\t', '\r', '\n', '"' };

    /// <summary>只產生 TSV；標頭列一定在，有沒有資料列由呼叫端判斷。</summary>
    public static string ToTsv<TRow>(IReadOnlyList<SqlTabularColumn<TRow>> columns, IEnumerable<TRow> rows)
    {
        Validate(columns, rows);
        var tsv = new StringBuilder();
        Write(columns, rows, tsv, html: null);
        return tsv.ToString();
    }

    /// <summary>同時產生 TSV 與 HTML；兩者的列與欄順序相同。</summary>
    public static SqlTabularContent Build<TRow>(IReadOnlyList<SqlTabularColumn<TRow>> columns, IEnumerable<TRow> rows)
    {
        Validate(columns, rows);
        var tsv = new StringBuilder();
        var html = new HtmlFragment();
        var count = Write(columns, rows, tsv, html);
        return new SqlTabularContent(tsv.ToString(), html.Complete(), count);
    }

    private static void Validate<TRow>(IReadOnlyList<SqlTabularColumn<TRow>> columns, IEnumerable<TRow> rows)
    {
        if (columns == null) throw new ArgumentNullException(nameof(columns));
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        if (columns.Count == 0) throw new ArgumentException("至少要有一欄。", nameof(columns));
    }

    private static int Write<TRow>(IReadOnlyList<SqlTabularColumn<TRow>> columns, IEnumerable<TRow> rows,
        StringBuilder tsv, HtmlFragment? html)
    {
        html?.BeginRow();
        for (var index = 0; index < columns.Count; index++)
        {
            var header = columns[index].Header;
            if (index > 0) tsv.Append('\t');
            AppendTsvCell(tsv, header);
            html?.Cell(header, heading: true);
        }

        tsv.Append(NewLine);
        html?.EndRow();

        var count = 0;
        foreach (var row in rows)
        {
            count++;
            html?.BeginRow();
            for (var index = 0; index < columns.Count; index++)
            {
                var value = columns[index].Value(row) ?? string.Empty;
                if (index > 0) tsv.Append('\t');
                AppendTsvCell(tsv, value);
                html?.Cell(value, heading: false);
            }

            tsv.Append(NewLine);
            html?.EndRow();
        }

        return count;
    }

    /// <summary>
    /// 值含定位字元、換行或引號時照 Excel 的規則包雙引號，並把引號重複一次；其餘原樣。
    /// </summary>
    /// <remarks>說明與 SQL 運算式可含換行；不包引號的話，一格會被貼成好幾列。</remarks>
    private static void AppendTsvCell(StringBuilder builder, string value)
    {
        if (value.IndexOfAny(QuotedCharacters) < 0)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (var character in value)
        {
            if (character == '"') builder.Append('"');
            builder.Append(character);
        }

        builder.Append('"');
    }

    private sealed class HtmlFragment
    {
        // 四個位移都預留十位數，寫完再回填：標頭長度因此固定，不必先知道內容多長。
        private const string Header =
            "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\n" +
            "StartFragment:0000000000\r\nEndFragment:0000000000\r\n";

        private const string Prefix = "<html><head><meta charset=\"utf-8\"></head><body>\r\n<!--StartFragment-->";

        private const string Suffix = "<!--EndFragment-->\r\n</body></html>";

        /// <summary>
        /// 格內換行。Excel 把一般的 <c>&lt;br&gt;</c> 當成下一列，貼上去一格變兩列；
        /// 這個樣式讓它留在同一格，Word 與郵件照樣當成換行。
        /// </summary>
        private const string CellBreak = "<br style=\"mso-data-placement:same-cell\">";

        private readonly StringBuilder _builder = new();
        private readonly int _startHtml;
        private readonly int _startFragment;

        /// <summary>目前寫了多少 UTF-8 位元組；位移欄位要的是位元組而不是字元。</summary>
        private int _bytes;

        public HtmlFragment()
        {
            Raw(Header);
            _startHtml = _bytes;
            Raw(Prefix);
            _startFragment = _bytes;
            Raw("<table>");
        }

        public void BeginRow() => Raw("<tr>");

        public void EndRow() => Raw("</tr>");

        public void Cell(string value, bool heading)
        {
            Raw(heading ? "<th>" : "<td>");
            Text(value);
            Raw(heading ? "</th>" : "</td>");
        }

        public string Complete()
        {
            Raw("</table>");
            var endFragment = _bytes;
            Raw(Suffix);
            Patch("StartHTML", _startHtml);
            Patch("EndHTML", _bytes);
            Patch("StartFragment", _startFragment);
            Patch("EndFragment", endFragment);
            return _builder.ToString();
        }

        /// <summary>只給標記用；標記全是 ASCII，一個字元就是一個位元組。</summary>
        private void Raw(string ascii)
        {
            _builder.Append(ascii);
            _bytes += ascii.Length;
        }

        private void Text(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                switch (character)
                {
                    case '&': Raw("&amp;"); break;
                    case '<': Raw("&lt;"); break;
                    case '>': Raw("&gt;"); break;
                    case '"': Raw("&quot;"); break;
                    case '\r':
                        if (index + 1 < value.Length && value[index + 1] == '\n') index++;
                        Raw(CellBreak);
                        break;
                    case '\n': Raw(CellBreak); break;
                    default:
                        if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                        {
                            _builder.Append(character).Append(value[++index]);
                            _bytes += 4;
                        }
                        else if (char.IsSurrogate(character))
                        {
                            // 落單的代理字元編成 UTF-8 時會被換成 U+FFFD（3 位元組）；照樣換掉，
                            // 位移才與剪貼簿實際收到的位元組對得上。
                            _builder.Append('�');
                            _bytes += 3;
                        }
                        else
                        {
                            _builder.Append(character);
                            _bytes += character < 0x80 ? 1 : character < 0x800 ? 2 : 3;
                        }

                        break;
                }
            }
        }

        private void Patch(string label, int value)
        {
            var start = Header.IndexOf(label + ":", StringComparison.Ordinal) + label.Length + 1;
            var digits = value.ToString("D10", CultureInfo.InvariantCulture);
            for (var index = 0; index < digits.Length; index++) _builder[start + index] = digits[index];
        }
    }
}
