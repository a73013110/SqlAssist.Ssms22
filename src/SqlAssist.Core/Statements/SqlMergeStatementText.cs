using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Statements;

/// <summary>
/// 把目標資料表的欄位排成一整句 <c>MERGE</c> 骨架。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlInsertStatementText"/> 同一條理由：使用者在 <c>MERGE INTO </c>
/// 之後選一張資料表，要的不會是「只把名稱補上」——那句話還沒寫完，而 MERGE 是
/// 三個子句都要逐欄重打的語句。舊的 Tab Stop 樣板一次只填得了一個欄位，
/// 而 <c>INSERT (…)</c> 那一格的欄位又與 <c>VALUES (source.…)</c> 那一格分開，
/// 十個欄位就是二十次 Tab。
///
/// 三個地方刻意保守：
///
/// <list type="bullet">
/// <item><c>ON</c> 用主索引鍵。沒有主索引鍵時留 <c>KeyColumn</c> 這個編譯不過的
/// 佔位字，不猜一個欄位——MERGE 的比對鍵猜錯不會報錯，只會把資料寫到別列去。</item>
/// <item>兩個動作子句都帶著 <c>AND 1 = 0</c>。展開出來的是一句立刻執行得動的
/// MERGE，而 MERGE 同時會改與插；沒有這個閘門，一次誤按 F5 就是一次資料事故。</item>
/// <item><c>UPDATE SET</c> 不含比對鍵：更新鍵本身沒有意義，而寫上去反而讓人以為
/// 那是刻意的。整張表都是鍵時整個 <c>WHEN MATCHED</c> 子句就不寫——MERGE 少一個
/// 動作子句仍然合法。</item>
/// </list>
/// </remarks>
public static class SqlMergeStatementText
{
    /// <summary>目標的別名；<c>USING</c> 那一邊固定叫 source。</summary>
    private const string TargetAlias = "target";

    private const string SourceAlias = "source";

    /// <summary>沒有主索引鍵時留在 <c>ON</c> 裡的佔位字。</summary>
    /// <remarks>
    /// 刻意選一個資料表裡不會有的名稱：這句 MERGE 因此編譯不過，使用者一定會
    /// 看到它。換成「猜一個欄位」的話語句跑得動，而跑錯的 MERGE 是資料事故。
    /// </remarks>
    public const string MissingKeyPlaceholder = "KeyColumn";

    /// <summary>來源資料表的佔位字；展開之後游標就停在它的起點。</summary>
    public const string SourcePlaceholder = "dbo.SourceTable";

    /// <summary>
    /// 組出 <c>MERGE INTO … USING … WHEN MATCHED … WHEN NOT MATCHED …</c>。
    /// </summary>
    /// <param name="qualifiedName">已經加好結構描述與方括號的目標資料表名稱。</param>
    /// <param name="keyColumns">
    /// 已經加好方括號的比對鍵；空的時候用 <see cref="MissingKeyPlaceholder"/>。
    /// </param>
    /// <param name="insertColumns">插得進去的欄位，順序就是輸出順序。</param>
    /// <param name="indent">第二行起每一行的前導文字。</param>
    /// <param name="newLine">緩衝區使用的換行字元。</param>
    /// <param name="caretOffset">
    /// 結果字串中來源資料表佔位字的位置。展開之後唯一還沒填的就是它。
    /// </param>
    public static string Build(
        string qualifiedName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> insertColumns,
        string indent,
        string newLine,
        out int caretOffset)
    {
        if (string.IsNullOrEmpty(qualifiedName))
        {
            throw new ArgumentException("資料表名稱不可為空。", nameof(qualifiedName));
        }

        if (keyColumns is null)
        {
            throw new ArgumentNullException(nameof(keyColumns));
        }

        if (insertColumns is null)
        {
            throw new ArgumentNullException(nameof(insertColumns));
        }

        if (insertColumns.Count == 0)
        {
            throw new ArgumentException("沒有欄位就組不出 MERGE。", nameof(insertColumns));
        }

        indent ??= string.Empty;
        newLine = string.IsNullOrEmpty(newLine) ? "\r\n" : newLine;

        var keys = keyColumns.Count > 0
            ? keyColumns
            : new[] { MissingKeyPlaceholder };
        var updateColumns = ExcludeKeys(insertColumns, keys);

        var body = indent + "    ";
        var builder = new StringBuilder();

        builder.Append("MERGE INTO ").Append(qualifiedName).Append(" AS ").Append(TargetAlias).Append(newLine);
        builder.Append(indent).Append("USING ");
        caretOffset = builder.Length;
        builder.Append(SourcePlaceholder).Append(" AS ").Append(SourceAlias).Append(newLine);

        for (var index = 0; index < keys.Count; index++)
        {
            builder.Append(body).Append(index == 0 ? "ON " : "AND ");
            AppendComparison(builder, keys[index]);
            builder.Append(newLine);
        }

        if (updateColumns.Count > 0)
        {
            builder.Append(indent).Append("WHEN MATCHED AND 1 = 0 THEN").Append(newLine);
            builder.Append(body).Append("UPDATE SET").Append(newLine);

            for (var index = 0; index < updateColumns.Count; index++)
            {
                builder.Append(body).Append("    ");
                AppendComparison(builder, updateColumns[index]);
                builder.Append(index == updateColumns.Count - 1 ? string.Empty : ",").Append(newLine);
            }
        }

        builder.Append(indent).Append("WHEN NOT MATCHED BY TARGET AND 1 = 0 THEN").Append(newLine);
        builder.Append(body).Append("INSERT").Append(newLine);
        AppendList(builder, insertColumns, body, indent, newLine, qualify: false);
        builder.Append(body).Append("VALUES").Append(newLine);
        AppendList(builder, insertColumns, body, indent, newLine, qualify: true);

        // 最後一行不換行：呼叫端替換的範圍結束在這裡，多一個換行會在編輯器裡
        // 留下一列空白，而使用者按復原時退回的是「只有名稱」，不是這一行。
        builder.Length -= newLine.Length;
        builder.Append(';');

        return builder.ToString();
    }

    /// <summary><c>target.欄位 = source.欄位</c>；兩邊同名是 MERGE 的常態寫法。</summary>
    private static void AppendComparison(StringBuilder builder, string column)
    {
        builder
            .Append(TargetAlias).Append('.').Append(column)
            .Append(" = ")
            .Append(SourceAlias).Append('.').Append(column);
    }

    /// <summary>括號包起來、每列一個欄位的清單。</summary>
    /// <remarks>
    /// 欄位清單與 VALUES 清單是<b>成對</b>的：第三個欄位對第三個值，
    /// 攤成一行就對不起來，而對不起來的代價是把值填錯格。理由與
    /// <see cref="SqlInsertStatementText"/> 相同，因此也一樣不跟萬用字元的排版設定走。
    /// </remarks>
    private static void AppendList(
        StringBuilder builder,
        IReadOnlyList<string> columns,
        string body,
        string indent,
        string newLine,
        bool qualify)
    {
        builder.Append(body).Append('(').Append(newLine);

        for (var index = 0; index < columns.Count; index++)
        {
            builder.Append(body).Append("    ");

            if (qualify)
            {
                builder.Append(SourceAlias).Append('.');
            }

            builder.Append(columns[index]);
            builder.Append(index == columns.Count - 1 ? string.Empty : ",").Append(newLine);
        }

        builder.Append(body).Append(')').Append(newLine);
    }

    private static IReadOnlyList<string> ExcludeKeys(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keys)
    {
        var result = new List<string>(columns.Count);

        foreach (var column in columns)
        {
            var isKey = false;

            foreach (var key in keys)
            {
                if (string.Equals(column, key, StringComparison.OrdinalIgnoreCase))
                {
                    isKey = true;
                    break;
                }
            }

            if (!isKey)
            {
                result.Add(column);
            }
        }

        return result;
    }
}
