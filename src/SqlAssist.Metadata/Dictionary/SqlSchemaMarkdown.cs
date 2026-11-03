using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 結構描述的 Markdown 版面：標題與脈絡、目錄，每張資料表一節一張表。
/// </summary>
/// <remarks>
/// 給要放進 Git 或 Wiki 的人：純文字、差異比對看得懂。儲存格裡的直線與換行會弄斷表格，
/// 直線跳脫成 <c>\|</c>、換行換成 <c>&lt;br&gt;</c>（GitHub、Azure DevOps 與多數 Wiki 都吃）。
/// 目錄的錨點照 GitHub 的規則由標題推出來，所以標題只放 <c>schema.name</c>。
/// </remarks>
public static class SqlSchemaMarkdown
{
    /// <summary>可為 NULL 那一格的記號；活頁簿用同一個。</summary>
    public const string Yes = "✓";

    public static void Write(SqlSchemaDocument document, TextWriter writer, Action<int>? tableWritten, CancellationToken cancellationToken)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        writer.WriteLine("# " + Escape(SchemaExportText.Title(document.DatabaseName)));
        writer.WriteLine();
        writer.WriteLine(Escape(document.Meta));
        writer.WriteLine();
        writer.WriteLine("## " + SchemaExportText.Contents);
        writer.WriteLine();
        Row(writer, SchemaExportText.HeaderNumber, SchemaExportText.HeaderTable, SchemaExportText.HeaderKind,
            SchemaExportText.HeaderColumns, SchemaExportText.HeaderDescription);
        writer.WriteLine("|---:|---|---|---:|---|");
        for (var i = 0; i < document.Tables.Count; i++)
        {
            var table = document.Tables[i];
            Row(writer, (i + 1).ToString(CultureInfo.InvariantCulture),
                "[" + Escape(table.Object.DisplayName) + "](#" + Anchor(table.Object.DisplayName) + ")",
                table.Kind, table.Columns.Count.ToString(CultureInfo.InvariantCulture), Cell(table.Description));
        }

        for (var i = 0; i < document.Tables.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteTable(writer, document.Tables[i]);
            tableWritten?.Invoke(i + 1);
        }

        writer.Flush();
    }

    private static void WriteTable(TextWriter writer, SqlSchemaTable table)
    {
        writer.WriteLine();
        writer.WriteLine("## " + Escape(table.Object.DisplayName));
        writer.WriteLine();
        if (table.Description.Length > 0)
        {
            writer.WriteLine(Cell(table.Description));
            writer.WriteLine();
        }

        writer.WriteLine("*" + Escape(table.Meta) + "*");
        writer.WriteLine();
        Row(writer, SchemaExportText.HeaderNumber, SchemaExportText.HeaderColumn, SchemaExportText.HeaderType,
            SchemaExportText.HeaderNullable, SchemaExportText.HeaderKey, SchemaExportText.HeaderDefault,
            SchemaExportText.HeaderTraits, SchemaExportText.HeaderReference, SchemaExportText.HeaderDescription);
        writer.WriteLine("|---:|---|---|:---:|:---:|---|---|---|---|");
        foreach (var column in table.Columns)
        {
            Row(writer,
                column.Ordinal.ToString(CultureInfo.InvariantCulture),
                Cell(column.Column.Name),
                Code(column.Column.DataType),
                column.Column.IsNullable ? Yes : "",
                column.Key,
                Code(column.Default),
                Cell(column.Traits),
                Code(column.Reference),
                Cell(column.Description));
        }
    }

    private static void Row(TextWriter writer, params string[] cells) => writer.WriteLine("| " + string.Join(" | ", cells) + " |");

    /// <summary>表格裡的一格：直線跳脫，換行換成 <c>&lt;br&gt;</c>。</summary>
    internal static string Cell(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Escape(text!).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>");

    /// <summary>行內程式碼；內容裡有反引號時用兩個反引號包，直線照樣跳脫。</summary>
    private static string Code(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var fence = text!.IndexOf('`') >= 0 ? "``" : "`";
        var body = text.Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return fence + (fence.Length > 1 ? " " + body + " " : body) + fence;
    }

    private static string Escape(string text) => text.Replace("|", "\\|");

    /// <summary>
    /// GitHub 的標題錨點：小寫、拿掉標點（保留字母、數字、底線、連字號與空白）、空白換成連字號。
    /// </summary>
    internal static string Anchor(string heading)
    {
        var anchor = new StringBuilder(heading.Length);
        foreach (var c in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') anchor.Append(c);
            else if (c == ' ') anchor.Append('-');
        }

        return anchor.ToString();
    }
}
