using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Statements;

/// <summary>
/// 把資料表的欄位排成一整句 <c>INSERT</c> 骨架。
/// </summary>
/// <remarks>
/// 欄位清單與 VALUES 清單一律每列一個，而且<b>不跟</b>展開萬用字元的排版設定走。
/// 那個設定的三種排法都在權衡「一行讀不讀得完」，這裡的兩份清單卻是<b>成對</b>的：
/// 第三個欄位對第三個值，攤成一行就對不起來了，而對不起來的代價是把值填錯格。
/// </remarks>
public static class SqlInsertStatementText
{
    /// <summary>
    /// 組出 <c>INSERT INTO 名稱 (欄位…) VALUES (值…)</c>。
    /// </summary>
    /// <param name="qualifiedName">已經加好結構描述與方括號的資料表名稱。</param>
    /// <param name="columns">插得進去的欄位，順序就是輸出順序。</param>
    /// <param name="indent">第二行起每一行的前導文字，通常是 <c>INSERT</c> 那一行的縮排。</param>
    /// <param name="newLine">緩衝區使用的換行字元。</param>
    /// <param name="caretOffset">
    /// 回傳結果字串中第一個值的位置。展開之後使用者要做的第一件事就是填第一個值，
    /// 把游標留在整段的結尾等於逼他自己捲回去。
    /// </param>
    public static string Build(
        string qualifiedName,
        IReadOnlyList<SqlStatementColumn> columns,
        string indent,
        string newLine,
        out int caretOffset)
    {
        if (string.IsNullOrEmpty(qualifiedName))
        {
            throw new ArgumentException("資料表名稱不可為空。", nameof(qualifiedName));
        }

        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("沒有欄位就組不出 INSERT。", nameof(columns));
        }

        indent ??= string.Empty;
        newLine = string.IsNullOrEmpty(newLine) ? "\r\n" : newLine;

        var body = indent + "    ";
        var values = new string[columns.Count];
        var widest = 0;

        for (var index = 0; index < columns.Count; index++)
        {
            values[index] = Literal(columns[index]);

            // 逗號要算進對齊寬度：最後一列沒有逗號，不算的話它的註解會凸出一格。
            var width = values[index].Length + (index == columns.Count - 1 ? 0 : 1);

            if (width > widest)
            {
                widest = width;
            }
        }

        var builder = new StringBuilder();
        builder.Append("INSERT INTO ").Append(qualifiedName).Append(newLine);
        builder.Append(indent).Append('(').Append(newLine);

        for (var index = 0; index < columns.Count; index++)
        {
            builder.Append(body).Append(columns[index].Name);
            builder.Append(index == columns.Count - 1 ? string.Empty : ",").Append(newLine);
        }

        builder.Append(indent).Append(')').Append(newLine);
        builder.Append(indent).Append("VALUES").Append(newLine);
        builder.Append(indent).Append('(').Append(newLine);

        caretOffset = builder.Length + body.Length;

        for (var index = 0; index < columns.Count; index++)
        {
            var value = values[index] + (index == columns.Count - 1 ? string.Empty : ",");
            builder.Append(body).Append(value);
            builder.Append(' ', widest - value.Length + 1);
            builder.Append("-- ").Append(columns[index].Name).Append(" - ").Append(columns[index].DataType);
            builder.Append(newLine);
        }

        builder.Append(indent).Append(')');
        return builder.ToString();
    }

    /// <summary>
    /// 這個欄位先填什麼。
    /// </summary>
    /// <remarks>
    /// 三條的順序不能對調。<c>VALUES (DEFAULT)</c> 對「沒有預設值而且 NOT NULL」的欄位
    /// 是執行期錯誤，所以 <c>DEFAULT</c> 只能給真的有 DEFAULT 條件約束的欄位；
    /// 剩下的可為 NULL 就填 <c>NULL</c>，都不是才輪到依型別給預留值。
    /// </remarks>
    private static string Literal(SqlStatementColumn column)
    {
        if (column.HasDefault)
        {
            return "DEFAULT";
        }

        return column.IsNullable ? "NULL" : SqlLiteralDefaults.ForType(column.DataType);
    }
}
