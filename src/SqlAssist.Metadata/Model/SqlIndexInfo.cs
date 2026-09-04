using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Model;

/// <summary>索引中的一個欄位。</summary>
public sealed class SqlIndexColumn
{
    public SqlIndexColumn(string name, bool isDescending = false, bool isIncluded = false)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("欄位名稱不可為空。", nameof(name));
        }

        Name = name;
        IsDescending = isDescending;
        IsIncluded = isIncluded;
    }

    public string Name { get; }

    public bool IsDescending { get; }

    /// <summary>是否為 INCLUDE 的非索引鍵欄位。</summary>
    public bool IsIncluded { get; }
}

/// <summary>
/// 索引查詢的一列原始結果。
/// </summary>
/// <remarks>
/// 一個索引會有多列（每個欄位一列），因此讀取與分組刻意分開：
/// <see cref="SqlMetadataReader"/> 只負責把一列對應成這個型別，
/// <see cref="SqlIndexInfo.FromRows"/> 負責合併，兩者都能不連資料庫就測試。
/// </remarks>
public sealed class SqlIndexRow
{
    public SqlIndexRow(
        int indexId,
        string name,
        bool isPrimaryKey,
        bool isUnique,
        bool isUniqueConstraint,
        string typeDescription,
        string? filterDefinition,
        string columnName,
        bool isDescending,
        bool isIncluded)
    {
        IndexId = indexId;
        Name = name;
        IsPrimaryKey = isPrimaryKey;
        IsUnique = isUnique;
        IsUniqueConstraint = isUniqueConstraint;
        TypeDescription = typeDescription ?? string.Empty;
        FilterDefinition = filterDefinition;
        ColumnName = columnName;
        IsDescending = isDescending;
        IsIncluded = isIncluded;
    }

    public int IndexId { get; }

    public string Name { get; }

    public bool IsPrimaryKey { get; }

    public bool IsUnique { get; }

    public bool IsUniqueConstraint { get; }

    public string TypeDescription { get; }

    public string? FilterDefinition { get; }

    public string ColumnName { get; }

    public bool IsDescending { get; }

    public bool IsIncluded { get; }
}

/// <summary>資料表或索引檢視的單一索引。</summary>
public sealed class SqlIndexInfo
{
    public SqlIndexInfo(
        int indexId,
        string name,
        IReadOnlyList<SqlIndexColumn> columns,
        bool isPrimaryKey = false,
        bool isUnique = false,
        bool isUniqueConstraint = false,
        string typeDescription = "NONCLUSTERED",
        string? filterDefinition = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("索引名稱不可為空。", nameof(name));
        }

        IndexId = indexId;
        Name = name;
        Columns = columns ?? Array.Empty<SqlIndexColumn>();
        IsPrimaryKey = isPrimaryKey;
        IsUnique = isUnique;
        IsUniqueConstraint = isUniqueConstraint;
        TypeDescription = typeDescription ?? string.Empty;
        FilterDefinition = filterDefinition;
    }

    public int IndexId { get; }

    public string Name { get; }

    /// <summary>索引鍵欄位在前、INCLUDE 欄位在後，順序與查詢回傳一致。</summary>
    public IReadOnlyList<SqlIndexColumn> Columns { get; }

    public bool IsPrimaryKey { get; }

    public bool IsUnique { get; }

    /// <summary>是否為 UNIQUE 條件約束而非單純的唯一索引。</summary>
    public bool IsUniqueConstraint { get; }

    /// <summary>sys.indexes.type_desc，例如 <c>CLUSTERED</c>。</summary>
    public string TypeDescription { get; }

    /// <summary>篩選索引的條件；一般索引為 null。</summary>
    public string? FilterDefinition { get; }

    /// <summary>把查詢回傳的扁平結果合併成索引清單，順序沿用輸入順序。</summary>
    public static IReadOnlyList<SqlIndexInfo> FromRows(IEnumerable<SqlIndexRow> rows)
    {
        if (rows is null)
        {
            throw new ArgumentNullException(nameof(rows));
        }

        var indexes = new List<SqlIndexInfo>();
        var columns = new List<SqlIndexColumn>();
        SqlIndexRow? current = null;

        foreach (var row in rows)
        {
            if (current is not null && row.IndexId != current.IndexId)
            {
                indexes.Add(Create(current, columns));
                columns = new List<SqlIndexColumn>();
            }

            current = row;
            columns.Add(new SqlIndexColumn(row.ColumnName, row.IsDescending, row.IsIncluded));
        }

        if (current is not null)
        {
            indexes.Add(Create(current, columns));
        }

        return indexes;
    }

    /// <summary>索引鍵欄位的簡短描述，例如 <c>Id ASC, Name DESC</c>。</summary>
    public string DescribeKeyColumns()
    {
        var builder = new StringBuilder();

        foreach (var column in Columns)
        {
            if (column.IsIncluded)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(column.Name).Append(column.IsDescending ? " DESC" : " ASC");
        }

        return builder.ToString();
    }

    /// <summary>INCLUDE 欄位的簡短描述；沒有時回傳空字串。</summary>
    public string DescribeIncludedColumns()
    {
        var builder = new StringBuilder();

        foreach (var column in Columns)
        {
            if (!column.IsIncluded)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(column.Name);
        }

        return builder.ToString();
    }

    /// <summary>索引種類的簡短描述，供清單顯示。</summary>
    public string DescribeKind()
    {
        if (IsPrimaryKey)
        {
            return $"PRIMARY KEY {TypeDescription}";
        }

        if (IsUniqueConstraint)
        {
            return $"UNIQUE CONSTRAINT {TypeDescription}";
        }

        return IsUnique ? $"UNIQUE {TypeDescription}" : TypeDescription;
    }

    /// <summary>
    /// 組出可以直接執行的建立語句。
    /// </summary>
    /// <remarks>
    /// 主索引鍵與唯一條件約束寫成 ALTER TABLE，其餘寫成 CREATE INDEX——
    /// 這兩者在 sys.indexes 裡長得一樣，但用錯寫法產生的指令碼不能執行。
    /// 主索引鍵在 <see cref="SqlObjectStructure"/> 裡是寫進 CREATE TABLE 的，
    /// 這裡的寫法供單獨複製某個索引時使用。
    /// </remarks>
    public string ToScript(string qualifiedObjectName)
    {
        var builder = new StringBuilder();
        var keys = BuildColumnList(included: false);

        if (IsPrimaryKey || IsUniqueConstraint)
        {
            builder.Append("ALTER TABLE ").Append(qualifiedObjectName)
                .Append(" ADD CONSTRAINT ").Append(SqlIdentifier.Quote(Name))
                .Append(IsPrimaryKey ? " PRIMARY KEY " : " UNIQUE ")
                .Append(TypeDescription)
                .Append(" (").Append(keys).Append(");");
            return builder.ToString();
        }

        builder.Append("CREATE ");

        if (IsUnique)
        {
            builder.Append("UNIQUE ");
        }

        builder.Append(TypeDescription).Append(" INDEX ").Append(SqlIdentifier.Quote(Name))
            .Append(" ON ").Append(qualifiedObjectName)
            .Append(" (").Append(keys).Append(')');

        var included = BuildColumnList(included: true);

        if (included.Length > 0)
        {
            builder.Append(" INCLUDE (").Append(included).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(FilterDefinition))
        {
            builder.Append(" WHERE ").Append(FilterDefinition);
        }

        builder.Append(';');
        return builder.ToString();
    }

    /// <summary>索引鍵欄位的括號內容，供 CREATE TABLE 的主索引鍵條件約束使用。</summary>
    public string BuildKeyColumnList() => BuildColumnList(included: false);

    private string BuildColumnList(bool included)
    {
        var builder = new StringBuilder();

        foreach (var column in Columns)
        {
            if (column.IsIncluded != included)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(SqlIdentifier.Quote(column.Name));

            if (!included)
            {
                builder.Append(column.IsDescending ? " DESC" : " ASC");
            }
        }

        return builder.ToString();
    }

    private static SqlIndexInfo Create(SqlIndexRow row, List<SqlIndexColumn> columns)
    {
        return new SqlIndexInfo(
            row.IndexId,
            row.Name,
            columns,
            row.IsPrimaryKey,
            row.IsUnique,
            row.IsUniqueConstraint,
            row.TypeDescription,
            row.FilterDefinition);
    }

    public override string ToString() => $"{DescribeKind()} {Name} ({DescribeKeyColumns()})";
}
