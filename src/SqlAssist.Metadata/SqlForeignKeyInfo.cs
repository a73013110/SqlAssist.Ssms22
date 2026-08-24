using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Metadata;

/// <summary>外來鍵中的一組欄位對應。</summary>
public sealed class SqlForeignKeyColumn
{
    public SqlForeignKeyColumn(string name, string referencedName)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("欄位名稱不可為空。", nameof(name));
        }

        if (string.IsNullOrEmpty(referencedName))
        {
            throw new ArgumentException("被參考的欄位名稱不可為空。", nameof(referencedName));
        }

        Name = name;
        ReferencedName = referencedName;
    }

    /// <summary>本資料表的欄位。</summary>
    public string Name { get; }

    /// <summary>被參考資料表的欄位。</summary>
    public string ReferencedName { get; }
}

/// <summary>外來鍵查詢的一列原始結果；複合鍵會有多列。</summary>
public sealed class SqlForeignKeyRow
{
    public SqlForeignKeyRow(
        string name,
        string referencedSchemaName,
        string referencedObjectName,
        string columnName,
        string referencedColumnName,
        string deleteAction,
        string updateAction)
    {
        Name = name;
        ReferencedSchemaName = referencedSchemaName;
        ReferencedObjectName = referencedObjectName;
        ColumnName = columnName;
        ReferencedColumnName = referencedColumnName;
        DeleteAction = deleteAction ?? string.Empty;
        UpdateAction = updateAction ?? string.Empty;
    }

    public string Name { get; }

    public string ReferencedSchemaName { get; }

    public string ReferencedObjectName { get; }

    public string ColumnName { get; }

    public string ReferencedColumnName { get; }

    public string DeleteAction { get; }

    public string UpdateAction { get; }
}

/// <summary>資料表的單一外來鍵。</summary>
public sealed class SqlForeignKeyInfo
{
    public SqlForeignKeyInfo(
        string name,
        string referencedSchemaName,
        string referencedObjectName,
        IReadOnlyList<SqlForeignKeyColumn> columns,
        string deleteAction = "NO_ACTION",
        string updateAction = "NO_ACTION")
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("外來鍵名稱不可為空。", nameof(name));
        }

        Name = name;
        ReferencedSchemaName = referencedSchemaName;
        ReferencedObjectName = referencedObjectName;
        Columns = columns ?? Array.Empty<SqlForeignKeyColumn>();
        DeleteAction = deleteAction ?? string.Empty;
        UpdateAction = updateAction ?? string.Empty;
    }

    public string Name { get; }

    public string ReferencedSchemaName { get; }

    public string ReferencedObjectName { get; }

    public IReadOnlyList<SqlForeignKeyColumn> Columns { get; }

    /// <summary>sys.foreign_keys.delete_referential_action_desc，例如 <c>CASCADE</c>。</summary>
    public string DeleteAction { get; }

    public string UpdateAction { get; }

    /// <summary>被參考物件的完整名稱。</summary>
    public string ReferencedQualifiedName =>
        $"{SqlIdentifier.Quote(ReferencedSchemaName)}.{SqlIdentifier.Quote(ReferencedObjectName)}";

    /// <summary>把查詢回傳的扁平結果合併成外來鍵清單，順序沿用輸入順序。</summary>
    public static IReadOnlyList<SqlForeignKeyInfo> FromRows(IEnumerable<SqlForeignKeyRow> rows)
    {
        if (rows is null)
        {
            throw new ArgumentNullException(nameof(rows));
        }

        var keys = new List<SqlForeignKeyInfo>();
        var columns = new List<SqlForeignKeyColumn>();
        SqlForeignKeyRow? current = null;

        foreach (var row in rows)
        {
            if (current is not null && !string.Equals(row.Name, current.Name, StringComparison.Ordinal))
            {
                keys.Add(Create(current, columns));
                columns = new List<SqlForeignKeyColumn>();
            }

            current = row;
            columns.Add(new SqlForeignKeyColumn(row.ColumnName, row.ReferencedColumnName));
        }

        if (current is not null)
        {
            keys.Add(Create(current, columns));
        }

        return keys;
    }

    /// <summary>欄位對應的簡短描述，例如 <c>UserId → [dbo].[Lib_Reader].Id</c>。</summary>
    public string DescribeColumns()
    {
        var source = new StringBuilder();
        var target = new StringBuilder();

        foreach (var column in Columns)
        {
            if (source.Length > 0)
            {
                source.Append(", ");
                target.Append(", ");
            }

            source.Append(column.Name);
            target.Append(column.ReferencedName);
        }

        return $"{source} → {ReferencedQualifiedName}.{target}";
    }

    /// <summary>參考動作的簡短描述；兩者都是 NO_ACTION 時回傳空字串。</summary>
    public string DescribeActions()
    {
        var builder = new StringBuilder();

        if (!IsNoAction(DeleteAction))
        {
            builder.Append("ON DELETE ").Append(DeleteAction.Replace('_', ' '));
        }

        if (!IsNoAction(UpdateAction))
        {
            if (builder.Length > 0)
            {
                builder.Append("  ");
            }

            builder.Append("ON UPDATE ").Append(UpdateAction.Replace('_', ' '));
        }

        return builder.ToString();
    }

    /// <summary>組出可以直接執行的 ALTER TABLE 語句。</summary>
    public string ToScript(string qualifiedObjectName)
    {
        var builder = new StringBuilder();
        builder.Append("ALTER TABLE ").Append(qualifiedObjectName)
            .Append(" ADD CONSTRAINT ").Append(SqlIdentifier.Quote(Name))
            .Append(" FOREIGN KEY (").Append(BuildColumnList(referenced: false)).Append(')')
            .Append(" REFERENCES ").Append(ReferencedQualifiedName)
            .Append(" (").Append(BuildColumnList(referenced: true)).Append(')');

        if (!IsNoAction(DeleteAction))
        {
            builder.Append(" ON DELETE ").Append(DeleteAction.Replace('_', ' '));
        }

        if (!IsNoAction(UpdateAction))
        {
            builder.Append(" ON UPDATE ").Append(UpdateAction.Replace('_', ' '));
        }

        builder.Append(';');
        return builder.ToString();
    }

    private string BuildColumnList(bool referenced)
    {
        var builder = new StringBuilder();

        foreach (var column in Columns)
        {
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(SqlIdentifier.Quote(referenced ? column.ReferencedName : column.Name));
        }

        return builder.ToString();
    }

    private static bool IsNoAction(string action)
    {
        return string.IsNullOrEmpty(action) ||
            string.Equals(action, "NO_ACTION", StringComparison.OrdinalIgnoreCase);
    }

    private static SqlForeignKeyInfo Create(SqlForeignKeyRow row, List<SqlForeignKeyColumn> columns)
    {
        return new SqlForeignKeyInfo(
            row.Name,
            row.ReferencedSchemaName,
            row.ReferencedObjectName,
            columns,
            row.DeleteAction,
            row.UpdateAction);
    }

    public override string ToString() => $"{Name}: {DescribeColumns()}";
}
