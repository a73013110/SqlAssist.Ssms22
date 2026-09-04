using System;
using System.Globalization;
using System.Text;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 把一塊查詢結果寫成建立暫存資料表並灌入資料的指令碼。
/// </summary>
/// <remarks>
/// 用途是把「線上那一份剛好出問題的資料」原封不動搬到一個可以反覆改、反覆查的
/// 暫存資料表裡。因此指令碼必須是<b>完整可執行</b>的一段：建表、灌資料、
/// 最後查一次確認——中間任何一步要使用者補手，這個功能就沒有省到事。
///
/// 型別照抄伺服器回報的那一個，不做推斷。從值反推型別是這個功能最容易出錯的地方：
/// 一整欄看起來都是整數，實際上是 <c>varchar</c> 而其中一列有前導零，
/// 推斷成 <c>int</c> 之後那一列的值就變了，而且 <c>INSERT</c> 不會失敗。
/// </remarks>
public static class SqlTempTableScript
{
    /// <summary>預設的暫存資料表名稱。</summary>
    public const string DefaultTableName = "#SqlAssistRows";

    /// <summary>
    /// 一個 <c>VALUES</c> 子句最多幾列。
    /// </summary>
    /// <remarks>
    /// T-SQL 的資料列建構函式硬性上限就是 1000；超過的訊息是
    /// 「The number of row value expressions in the INSERT statement exceeds
    /// the maximum allowed number of 1000 row values.」——指令碼產得出來、
    /// 貼得上去，執行才失敗，所以要在這裡就切開。
    /// </remarks>
    public const int MaxRowsPerInsert = 1000;

    /// <summary>產不出來時的第一句。</summary>
    private const string UnavailableHeadline = "無法從查詢結果產生暫存資料表指令碼。";

    public static string Build(ResultGridTable table, string? tableName = null)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        var name = string.IsNullOrWhiteSpace(tableName) ? DefaultTableName : tableName!.Trim();

        if (table.IsEmpty)
        {
            return ResultGridLiterals.Unavailable(
                UnavailableHeadline,
                "選取範圍裡沒有資料列，或這份結果沒有欄位。",
                "先在結果格線裡選幾格，再執行一次這個命令。");
        }

        // 先轉字面值再描述欄位，不是相反：問不出精確度的 decimal 欄要從實際的值
        // 反推小數位數（見 SqlTempTableColumnType），而那份值就是這裡轉出來的。
        if (!ResultGridLiterals.TryFormatAll(table, out var literals, out var valueFailure))
        {
            return ResultGridLiterals.Unavailable(
                UnavailableHeadline,
                valueFailure,
                "把那一欄從選取範圍拿掉之後再試一次；其餘欄位都轉得出來。");
        }

        if (!TryDescribeColumns(table, literals, out var definitions, out var typeFailure))
        {
            return ResultGridLiterals.Unavailable(UnavailableHeadline, typeFailure);
        }

        var builder = new StringBuilder(ResultGridLiterals.EstimateLength(literals) + 512);

        ResultGridLiterals.AppendSourceComment(builder, table);
        builder.AppendLine("-- 值取自結果格線已經取回的資料，不會重新查詢資料庫。");
        builder.AppendLine();

        AppendCreate(builder, name, definitions);
        AppendInserts(builder, table, name, literals);

        builder.Append("SELECT * FROM ").Append(name).AppendLine(";");
        return builder.ToString();
    }

    /// <summary>每一欄的名稱與型別；任何一欄沒有型別就整段拒絕。</summary>
    private static bool TryDescribeColumns(
        ResultGridTable table,
        string[][] literals,
        out (string Name, string Type)[] definitions,
        out string failure)
    {
        var names = table.ScriptColumnNames;
        definitions = new (string, string)[table.Columns.Count];
        failure = string.Empty;

        for (var index = 0; index < table.Columns.Count; index++)
        {
            var type = SqlTempTableColumnType.For(
                table.Columns[index],
                ColumnLiterals(literals, index));

            if (type.Length == 0)
            {
                failure = string.Format(
                    CultureInfo.InvariantCulture,
                    "取不到「{0}」欄的伺服器型別，沒有型別就寫不出 CREATE TABLE。"
                    + "運算式欄位有時候查不到型別，替它取一個別名再執行一次查詢通常就有了。",
                    names[index]);
                definitions = Array.Empty<(string, string)>();
                return false;
            }

            definitions[index] = (SqlIdentifier.QuoteIfNeeded(names[index]), type);
        }

        return true;
    }

    /// <summary>把一欄的字面值從列優先的陣列裡挑出來。</summary>
    private static string[] ColumnLiterals(string[][] literals, int index)
    {
        var column = new string[literals.Length];

        for (var row = 0; row < literals.Length; row++)
        {
            column[row] = index < literals[row].Length ? literals[row][index] : string.Empty;
        }

        return column;
    }

    /// <remarks>
    /// 一律加上 <c>DROP</c> 的守門：這個指令碼的用法就是改一改再跑一次，
    /// 而第二次執行時暫存資料表還在，少了這一段就會失敗。
    ///
    /// 所有欄位都寫成允許 <c>NULL</c>。格線知道的是「這一次查到的資料」，
    /// 不是欄位的可為空性；照觀察到的值推斷 <c>NOT NULL</c>，
    /// 會在使用者改資料重跑的時候莫名其妙失敗。
    /// </remarks>
    private static void AppendCreate(
        StringBuilder builder,
        string tableName,
        (string Name, string Type)[] definitions)
    {
        builder.Append("IF OBJECT_ID('tempdb..").Append(tableName).AppendLine("') IS NOT NULL");
        builder.Append("    DROP TABLE ").Append(tableName).AppendLine(";");
        builder.AppendLine();

        var width = 0;

        foreach (var definition in definitions)
        {
            if (definition.Name.Length > width)
            {
                width = definition.Name.Length;
            }
        }

        builder.Append("CREATE TABLE ").AppendLine(tableName);
        builder.AppendLine("(");

        for (var index = 0; index < definitions.Length; index++)
        {
            builder.Append("    ").Append(definitions[index].Name)
                .Append(' ', width - definitions[index].Name.Length + 1)
                .Append(definitions[index].Type).Append(" NULL")
                .AppendLine(index == definitions.Length - 1 ? string.Empty : ",");
        }

        builder.AppendLine(");");
        builder.AppendLine();
    }

    private static void AppendInserts(
        StringBuilder builder,
        ResultGridTable table,
        string tableName,
        string[][] literals)
    {
        for (var start = 0; start < literals.Length; start += MaxRowsPerInsert)
        {
            var end = Math.Min(start + MaxRowsPerInsert, literals.Length);

            builder.Append("INSERT INTO ").Append(tableName).Append(" (");
            ResultGridLiterals.AppendQuotedNames(builder, table.ScriptColumnNames);
            builder.AppendLine(")");
            builder.AppendLine("VALUES");

            for (var row = start; row < end; row++)
            {
                builder.Append("    (");

                for (var column = 0; column < literals[row].Length; column++)
                {
                    if (column > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(literals[row][column]);
                }

                builder.Append(')').AppendLine(row == end - 1 ? ";" : ",");
            }

            builder.AppendLine();
        }
    }
}
