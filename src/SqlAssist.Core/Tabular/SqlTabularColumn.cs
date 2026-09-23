using System;

namespace SqlAssist.Core.Tabular;

/// <summary>表格文字的一欄：標頭與從一列取出的值。</summary>
/// <remarks>
/// 欄位定義留在各功能自己的檔案：哪幾欄、叫什麼、時間怎麼印是領域的事，
/// 產生器只管「一格字怎麼寫進 TSV 與 HTML」。
/// </remarks>
public sealed class SqlTabularColumn<TRow>
{
    /// <param name="value">回 null 視為空格，不印出「null」。</param>
    public SqlTabularColumn(string header, Func<TRow, string?> value)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Header { get; }

    public Func<TRow, string?> Value { get; }
}
