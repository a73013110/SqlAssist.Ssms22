using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 物件的完整結構：第二層的欄位與參數，加上只有結構面板才需要的索引與外來鍵。
/// </summary>
/// <remarks>
/// 索引與外來鍵刻意不放進 <see cref="SqlObjectDetail"/>：第二層在按鍵路徑上，
/// 使用者輸入 <c>a.</c> 要的是欄位清單，為此多付兩次查詢並不值得。
/// 這一層只有使用者主動打開結構面板時才載入，那時等得起。
/// </remarks>
public sealed class SqlObjectStructure
{
    private static readonly SqlIndexInfo[] NoIndexes = Array.Empty<SqlIndexInfo>();
    private static readonly SqlForeignKeyInfo[] NoForeignKeys = Array.Empty<SqlForeignKeyInfo>();

    public SqlObjectStructure(
        SqlObjectDetail detail,
        IReadOnlyList<SqlIndexInfo>? indexes = null,
        IReadOnlyList<SqlForeignKeyInfo>? foreignKeys = null)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Indexes = indexes ?? NoIndexes;
        ForeignKeys = foreignKeys ?? NoForeignKeys;
    }

    public SqlObjectDetail Detail { get; }

    public SqlObjectInfo Object => Detail.Object;

    public IReadOnlyList<SqlColumnInfo> Columns => Detail.Columns;

    public IReadOnlyList<SqlParameterInfo> Parameters => Detail.Parameters;

    public string? Definition => Detail.Definition;

    public IReadOnlyList<SqlIndexInfo> Indexes { get; }

    public IReadOnlyList<SqlForeignKeyInfo> ForeignKeys { get; }

    /// <summary>主索引鍵；沒有時為 null。</summary>
    public SqlIndexInfo? PrimaryKey
    {
        get
        {
            foreach (var index in Indexes)
            {
                if (index.IsPrimaryKey)
                {
                    return index;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 組出可以直接執行的完整指令碼。
    /// </summary>
    /// <remarks>
    /// 模組類物件直接給定義本文——那本來就是可執行的原文，重組只會失真。
    /// 資料表則重建 CREATE TABLE，並把主索引鍵寫進條件約束，
    /// 其餘索引與外來鍵接在後面，順序與 SSMS 的指令碼一致。
    /// </remarks>
    public string BuildScript()
    {
        if (Object.Kind.IsModule() && !string.IsNullOrWhiteSpace(Definition))
        {
            return Definition!;
        }

        if (!Object.Kind.HasColumns() && Columns.Count == 0)
        {
            return Detail.BuildPreview();
        }

        var builder = new StringBuilder();
        var name = Object.QualifiedName;
        builder.Append("CREATE TABLE ").Append(name).AppendLine();
        builder.AppendLine("(");

        for (var index = 0; index < Columns.Count; index++)
        {
            builder.Append("    ").Append(BuildColumnDefinition(Columns[index]));
            builder.AppendLine(index == Columns.Count - 1 && PrimaryKey is null ? string.Empty : ",");
        }

        if (PrimaryKey is { } primaryKey)
        {
            builder.Append("    CONSTRAINT ").Append(SqlIdentifier.Quote(primaryKey.Name))
                .Append(" PRIMARY KEY ").Append(primaryKey.TypeDescription)
                .Append(" (").Append(primaryKey.BuildKeyColumnList()).AppendLine(")");
        }

        builder.AppendLine(");");

        foreach (var index in Indexes)
        {
            if (index.IsPrimaryKey)
            {
                continue;
            }

            builder.AppendLine();
            builder.AppendLine(index.ToScript(name));
        }

        foreach (var foreignKey in ForeignKeys)
        {
            builder.AppendLine();
            builder.AppendLine(foreignKey.ToScript(name));
        }

        return builder.ToString();
    }

    /// <summary>
    /// CREATE TABLE 內的單行欄位定義。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="SqlColumnInfo.ToScriptLine"/> 的差別在於這裡要能執行：
    /// 不加 <c>-- PK</c> 註解（主索引鍵另外寫成條件約束），
    /// 計算欄位也不能寫型別，否則整段指令碼貼上去就會失敗。
    /// </remarks>
    private static string BuildColumnDefinition(SqlColumnInfo column)
    {
        var builder = new StringBuilder();
        builder.Append(SqlIdentifier.Quote(column.Name)).Append(' ');

        if (column.IsComputed)
        {
            builder.Append("AS ").Append(column.ComputedDefinition ?? "(/* 無法取得運算式 */)");
            return builder.ToString();
        }

        builder.Append(column.DataType);

        if (column.IsIdentity)
        {
            builder.Append(" IDENTITY");
        }

        builder.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (!string.IsNullOrEmpty(column.DefaultDefinition))
        {
            builder.Append(" DEFAULT ").Append(column.DefaultDefinition);
        }

        return builder.ToString();
    }
}
