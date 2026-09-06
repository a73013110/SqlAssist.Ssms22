using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Analysis;

/// <summary>
/// SCHEMA-001：<c>INCLUDE</c> 裡帶了大型物件資料行。
/// </summary>
/// <remarks>
/// <c>INCLUDE</c> 的資料行整份複製進索引的分葉層。帶一個
/// <c>nvarchar(max)</c> 進去，那個索引會跟著資料長成與資料表同一個量級，
/// 而它本來的用處是「小而快」。這一條幾乎不會是刻意的。
/// </remarks>
public sealed class SqlLargeObjectInIncludeRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-001";

    public string Title => "INCLUDE 帶了大型物件資料行";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        var columns = SqlSchemaColumnFacts.ByName(structure);

        foreach (var index in structure.Indexes)
        {
            foreach (var column in index.Columns)
            {
                if (!column.IsIncluded ||
                    !columns.TryGetValue(column.Name, out var info) ||
                    !SqlTypeName.IsLargeObject(info.DataType))
                {
                    continue;
                }

                yield return new SqlSchemaFinding(
                    Id,
                    SqlSchemaSeverity.Warning,
                    $"INCLUDE 帶了 {column.Name} {info.DataType}，索引會跟著資料長成與資料表同一個量級。",
                    index.Name);
            }
        }
    }
}

/// <summary>
/// SCHEMA-002：索引鍵是另一個索引的前綴。
/// </summary>
/// <remarks>
/// 前綴重疊的索引多半可以直接刪掉：查詢用得到 <c>(A)</c> 的時候，
/// <c>(A, B)</c> 一樣用得到。留著的代價是每一次寫入都要多維護一份。
///
/// 主索引鍵與唯一索引<b>不</b>報：它們同時是條件約束，刪不掉也不該刪。
/// </remarks>
public sealed class SqlRedundantIndexRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-002";

    public string Title => "索引鍵是另一個索引的前綴";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        var keys = new List<(SqlIndexInfo Index, List<string> Columns)>();

        foreach (var index in structure.Indexes)
        {
            keys.Add((index, SqlSchemaColumnFacts.KeyColumnNames(index)));
        }

        foreach (var (index, columns) in keys)
        {
            // 主索引鍵與唯一索引同時是條件約束，刪不掉也不該刪。
            if (index.IsPrimaryKey || index.IsUnique || index.IsUniqueConstraint || columns.Count == 0)
            {
                continue;
            }

            foreach (var (other, otherColumns) in keys)
            {
                if (ReferenceEquals(index, other) || !IsPrefix(columns, otherColumns))
                {
                    continue;
                }

                yield return new SqlSchemaFinding(
                    Id,
                    SqlSchemaSeverity.Warning,
                    $"索引鍵 ({string.Join(", ", columns)}) 是 {other.Name} 的前綴，多半可以刪掉。",
                    index.Name);

                break;
            }
        }
    }

    /// <remarks>
    /// 完全相同也算前綴——兩個索引鍵一模一樣的索引是最該刪的那一種。
    /// </remarks>
    private static bool IsPrefix(List<string> candidate, List<string> longer)
    {
        if (candidate.Count > longer.Count)
        {
            return false;
        }

        for (var index = 0; index < candidate.Count; index++)
        {
            if (!string.Equals(candidate[index], longer[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// SCHEMA-003：沒有叢集索引的堆積資料表。
/// </summary>
/// <remarks>
/// 堆積不是錯，暫存與純附加的資料表刻意用它；但一張有主索引鍵、有外來鍵的
/// 業務資料表是堆積，多半是當初忘了，而症狀要等到資料長大、刪改變多之後
/// 才以「查詢突然變慢」的樣子出現。
/// </remarks>
public sealed class SqlHeapTableRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-003";

    public string Title => "資料表沒有叢集索引";

    private const string Clustered = "CLUSTERED";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        if (structure.Object.Kind != SqlObjectKind.Table || structure.Columns.Count == 0)
        {
            yield break;
        }

        foreach (var index in structure.Indexes)
        {
            if (index.TypeDescription.Equals(Clustered, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }
        }

        yield return new SqlSchemaFinding(
            Id,
            SqlSchemaSeverity.Warning,
            "這張資料表沒有叢集索引（堆積）。");
    }
}
