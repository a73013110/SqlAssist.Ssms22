using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Analysis;

/// <summary>
/// SCHEMA-004：資料表沒有主索引鍵。
/// </summary>
/// <remarks>
/// 沒有主索引鍵的資料表沒有辦法穩定地指出「哪一列」，複寫、變更追蹤與大多數
/// ORM 都用不上；而補一個上去要等到有重複資料之後才會發現補不了。
/// </remarks>
public sealed class SqlMissingPrimaryKeyRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-004";

    public string Title => "資料表沒有主索引鍵";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        // 資料行一列都沒有回來時不報：那是「這一輪沒有資料」，不是這張表的問題。
        if (structure.Object.Kind != SqlObjectKind.Table ||
            structure.Columns.Count == 0 ||
            structure.PrimaryKey is not null)
        {
            yield break;
        }

        yield return new SqlSchemaFinding(
            Id,
            SqlSchemaSeverity.Error,
            "這張資料表沒有主索引鍵。");
    }
}

/// <summary>
/// SCHEMA-005：同語意的資料行型別不一致。
/// </summary>
/// <remarks>
/// 依名稱最後一個駝峰詞分組（<c>LoanUser</c> 與 <c>CreateUser</c> 都是
/// <c>User</c>），同組之內型別不同就報。混用 <c>varchar</c> 與 <c>nvarchar</c>
/// 的代價不只是儲存空間：兩者比較時會發生隱含轉換，而那會讓索引用不上。
///
/// 只在<b>同一個型別大類</b>裡比。<c>LoanId int</c> 與 <c>PublicId uniqueidentifier</c>
/// 都以 <c>Id</c> 結尾，但那個差異多半是刻意的——一個是流水號一個是對外的識別碼，
/// 報出來只是雜訊，而雜訊會讓整個健檢被關掉。
/// </remarks>
public sealed class SqlInconsistentColumnTypeRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-005";

    public string Title => "同語意的資料行型別不一致";

    /// <summary>後綴短於這個長度就不分組：<c>o</c>、<c>e</c> 這種湊出來的組沒有意義。</summary>
    private const int MinimumSuffixLength = 2;

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        var groups = new Dictionary<string, List<SqlColumnInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in structure.Columns)
        {
            var suffix = SqlSchemaColumnFacts.SuffixOf(column.Name);

            if (suffix.Length < MinimumSuffixLength ||
                string.Equals(suffix, column.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!groups.TryGetValue(suffix, out var members))
            {
                members = new List<SqlColumnInfo>();
                groups[suffix] = members;
            }

            members.Add(column);
        }

        foreach (var pair in groups)
        {
            foreach (var finding in Compare(pair.Key, pair.Value))
            {
                yield return finding;
            }
        }
    }

    private IEnumerable<SqlSchemaFinding> Compare(string suffix, List<SqlColumnInfo> members)
    {
        if (members.Count < 2)
        {
            yield break;
        }

        var reference = members[0];
        var family = SqlTypeName.FamilyOf(reference.DataType);

        foreach (var column in members)
        {
            if (SqlTypeName.FamilyOf(column.DataType) != family ||
                family == SqlTypeName.Family.Other ||
                string.Equals(column.DataType, reference.DataType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new SqlSchemaFinding(
                Id,
                SqlSchemaSeverity.Warning,
                $"與同樣以 {suffix} 結尾的 {reference.Name} {reference.DataType} 型別不同；" +
                "比較時的隱含轉換會讓索引用不上。",
                $"{column.Name} {column.DataType}");
        }
    }
}

/// <summary>
/// SCHEMA-006：列舉語意的資料行沒有 <c>CHECK</c>。
/// </summary>
/// <remarks>
/// 認的是「小整數或單字元，而且說明裡寫了 <c>=</c>」這個組合——
/// <c>狀態：1=預約, 2=借出, 3=歸還</c> 那種寫法。說明既然列得出有哪幾個值，
/// 那幾個值就是這個資料行的定義域，而定義域沒有寫進資料庫的話，
/// 第四個值遲早會進來。
///
/// 沒有說明的資料行不報：那時無從判斷它是不是列舉。
/// </remarks>
public sealed class SqlEnumWithoutCheckRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-006";

    public string Title => "列舉語意的資料行沒有 CHECK 條件約束";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        var descriptions = SqlSchemaColumnFacts.DescriptionsByColumn(structure);

        foreach (var column in structure.Columns)
        {
            if (!LooksLikeEnum(column) ||
                !descriptions.TryGetValue(column.Name, out var description) ||
                description.IndexOf('=') < 0 ||
                HasCheck(structure, column.Name))
            {
                continue;
            }

            yield return new SqlSchemaFinding(
                Id,
                SqlSchemaSeverity.Warning,
                "說明列出了有哪幾個值，卻沒有 CHECK 條件約束把定義域寫進資料庫。",
                column.Name);
        }
    }

    private static bool LooksLikeEnum(SqlColumnInfo column)
    {
        switch (SqlTypeName.BaseOf(column.DataType))
        {
            case "tinyint":
            case "smallint":
                return true;
            case "char":
            case "nchar":
            case "varchar":
            case "nvarchar":
                return column.DataType.EndsWith("(1)", StringComparison.Ordinal);
            default:
                return false;
        }
    }

    /// <remarks>
    /// 比對的是運算式裡有沒有 <c>[資料行]</c>。條件約束的定義由伺服器組回來，
    /// 資料行名稱一律帶方括號，所以這個比對不必自己剖析運算式。
    /// </remarks>
    private static bool HasCheck(SqlObjectStructure structure, string columnName)
    {
        var quoted = "[" + columnName + "]";

        foreach (var check in structure.CheckConstraints)
        {
            if (string.Equals(check.ColumnName, columnName, StringComparison.OrdinalIgnoreCase) ||
                check.Definition.IndexOf(quoted, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// SCHEMA-007：名稱看起來是外來鍵，卻沒有外來鍵。
/// </summary>
/// <remarks>
/// 以 <c>Id</c> 或 <c>No</c> 結尾、又不是這張表自己的鍵，多半是指向別張表的。
/// 沒有宣告成外來鍵的代價是孤兒資料：刪掉主表那一列時沒有人擋，
/// 而發現的時候通常是報表少了幾筆。
///
/// 本表自己的鍵（主索引鍵，以及單一資料行的唯一索引）要先排掉——
/// 漏掉這一半的話，一個 <c>PublicId</c> 會被當成疑似外來鍵報出來，
/// 而那正是使用者最先失去信任的那種誤報。
/// </remarks>
public sealed class SqlMissingForeignKeyRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-007";

    public string Title => "名稱像外來鍵卻沒有外來鍵";

    private static readonly string[] Suffixes = { "Id", "No" };

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        if (structure.Object.Kind != SqlObjectKind.Table)
        {
            yield break;
        }

        var keys = SqlSchemaColumnFacts.KeyColumns(structure);
        var referenced = ForeignKeyColumns(structure);

        foreach (var column in structure.Columns)
        {
            if (column.IsIdentity ||
                column.IsComputed ||
                keys.Contains(column.Name) ||
                referenced.Contains(column.Name) ||
                !LooksLikeReference(column.Name))
            {
                continue;
            }

            yield return new SqlSchemaFinding(
                Id,
                SqlSchemaSeverity.Information,
                "名稱看起來指向另一張資料表，卻沒有外來鍵擋住孤兒資料。",
                column.Name);
        }
    }

    private static bool LooksLikeReference(string name)
    {
        foreach (var suffix in Suffixes)
        {
            // 整個名稱就是後綴時不算：一張表叫 Id 的資料行是它自己的鍵。
            if (name.Length > suffix.Length &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> ForeignKeyColumns(SqlObjectStructure structure)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var foreignKey in structure.ForeignKeys)
        {
            foreach (var column in foreignKey.Columns)
            {
                result.Add(column.Name);
            }
        }

        return result;
    }
}
