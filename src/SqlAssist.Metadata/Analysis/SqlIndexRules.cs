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
/// 只提示候選重疊，不把鍵前綴相同當成可刪除的證據：篩選範圍、排序與
/// INCLUDE 都可能是保留索引的理由，真正取捨仍須看查詢計畫與工作負載。
///
/// 主索引鍵與唯一索引<b>不</b>報：它們同時是條件約束，刪不掉也不該刪。
/// </remarks>
public sealed class SqlRedundantIndexRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-002";

    public string Title => "索引鍵是另一個索引的前綴";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        var keys = new List<IndexFacts>();

        foreach (var index in structure.Indexes)
        {
            if (!index.Options.IsDisabled && index.TypeDescription.Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(new IndexFacts(index));
            }
        }

        foreach (var candidate in keys)
        {
            var index = candidate.Index;
            var columns = candidate.Keys;
            // 主索引鍵與唯一索引同時是條件約束，刪不掉也不該刪。
            if (index.IsPrimaryKey || index.IsUnique || index.IsUniqueConstraint || columns.Count == 0)
            {
                continue;
            }

            foreach (var other in keys)
            {
                if (ReferenceEquals(index, other.Index) || !IsPrefix(columns, other.Keys) ||
                    !string.Equals(index.FilterDefinition ?? string.Empty, other.Index.FilterDefinition ?? string.Empty, StringComparison.Ordinal) ||
                    !candidate.Available.IsSubsetOf(other.Available))
                {
                    continue;
                }

                // 完全相同的兩個普通索引只報一個，避免兩則建議互相把對方當成可保留者。
                if (!other.Index.IsUnique && !other.Index.IsPrimaryKey && !other.Index.IsUniqueConstraint &&
                    columns.Count == other.Keys.Count && candidate.Available.SetEquals(other.Available) &&
                    (index.IndexId < other.Index.IndexId ||
                        (index.IndexId == other.Index.IndexId && string.CompareOrdinal(index.Name, other.Index.Name) <= 0)))
                {
                    continue;
                }

                yield return new SqlSchemaFinding(
                    Id,
                    SqlSchemaSeverity.Warning,
                    $"索引鍵 ({index.DescribeKeyColumns()}) 與 {other.Index.Name} 重疊；" +
                    "篩選、排序與涵蓋欄位相容，可評估整併，仍須先驗證查詢計畫與工作負載。",
                    index.Name);

                break;
            }
        }
    }

    /// <remarks>
    /// 完全相同也算前綴；是否值得整併仍由篩選與涵蓋欄位的檢查決定。
    /// </remarks>
    private static bool IsPrefix(List<SqlIndexColumn> candidate, List<SqlIndexColumn> longer)
    {
        if (candidate.Count > longer.Count)
        {
            return false;
        }

        for (var index = 0; index < candidate.Count; index++)
        {
            if (candidate[index].IsDescending != longer[index].IsDescending ||
                !string.Equals(candidate[index].Name, longer[index].Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class IndexFacts
    {
        public IndexFacts(SqlIndexInfo index)
        {
            Index = index;
            foreach (var column in index.Columns)
            {
                Available.Add(column.Name);
                if (!column.IsIncluded)
                {
                    Keys.Add(column);
                }
            }
        }

        public SqlIndexInfo Index { get; }
        public List<SqlIndexColumn> Keys { get; } = new();
        public HashSet<string> Available { get; } = new(StringComparer.OrdinalIgnoreCase);
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
            if (index.TypeDescription.Equals(Clustered, StringComparison.OrdinalIgnoreCase) ||
                index.TypeDescription.Equals("CLUSTERED COLUMNSTORE", StringComparison.OrdinalIgnoreCase))
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
