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
        // 模組的分支必須整個接走，不能只在「拿得到定義」時接。檢視同時是模組
        // 也有欄位，定義取不到時原本會掉進下面的 CREATE TABLE，於是一個檢視被
        // 寫成一張資料表——那不只是排版難看，是指令碼在說謊：照著執行會多出
        // 一張同名的資料表。取不到定義的兩個原因寫在輸出裡，否則使用者只看得到
        // 「這個物件沒有指令碼」而查不出為什麼。
        if (Object.Kind.IsModule())
        {
            return string.IsNullOrWhiteSpace(Definition)
                ? BuildMissingDefinitionScript()
                : Definition!;
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
    /// 取不到定義的模組：說明原因，並把查得到的欄位與參數以註解列出來。
    /// </summary>
    /// <remarks>
    /// 整段都是註解，因為這裡沒有一行是可以執行的。猜一個 CREATE VIEW 的骨架
    /// 出來反而更糟——那是本擴充沒有讀到的東西，與 <c>SELECT *</c> 不做部分展開
    /// 是同一條理由。
    /// </remarks>
    private string BuildMissingDefinitionScript()
    {
        var builder = new StringBuilder();
        builder.Append("-- 取不到 ").Append(Object.QualifiedName).AppendLine(" 的定義。");
        builder.AppendLine("-- OBJECT_DEFINITION 傳回 NULL 的原因只有兩個：物件是 WITH ENCRYPTION 建立的，");
        builder.AppendLine("-- 或是目前的登入沒有它的 VIEW DEFINITION 權限。");

        if (Columns.Count > 0)
        {
            builder.AppendLine();
            builder.Append("-- ").Append(Object.Kind.ToDisplayName()).Append(" 的欄位（")
                .Append(Columns.Count).AppendLine(" 個）：");

            foreach (var column in Columns)
            {
                builder.Append("--     ").AppendLine(column.ToScriptLine());
            }
        }

        if (Parameters.Count > 0)
        {
            builder.AppendLine();
            builder.Append("-- 參數（").Append(Parameters.Count).AppendLine(" 個）：");

            foreach (var parameter in Parameters)
            {
                builder.Append("--     ").AppendLine(parameter.ToScriptLine());
            }
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
