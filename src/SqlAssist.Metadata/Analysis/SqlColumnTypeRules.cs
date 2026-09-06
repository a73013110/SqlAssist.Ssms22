using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Analysis;

/// <summary>
/// SCHEMA-008：<c>datetime</c> 建議改用 <c>datetime2</c>。
/// </summary>
/// <remarks>
/// <c>datetime</c> 的精確度是 3.33 毫秒，而且會把時間<b>四捨五入</b>到那個刻度上；
/// <c>datetime2(3)</c> 佔的位元組更少、範圍更大，精確度還可以自己指定。
/// 這是建議不是錯誤：既有資料表換型別要動到所有讀寫它的地方。
/// </remarks>
public sealed class SqlLegacyDateTimeRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-008";

    public string Title => "datetime 建議改用 datetime2";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        foreach (var column in structure.Columns)
        {
            if (!string.Equals(SqlTypeName.BaseOf(column.DataType), "datetime", StringComparison.Ordinal))
            {
                continue;
            }

            yield return new SqlSchemaFinding(
                Id,
                SqlSchemaSeverity.Information,
                "datetime 會把時間四捨五入到 3.33 毫秒；datetime2 佔的位元組更少、範圍更大。",
                column.Name);
        }
    }
}

/// <summary>
/// SCHEMA-009：<c>text</c>、<c>ntext</c> 與 <c>image</c> 建議改用 <c>max</c> 型別。
/// </summary>
/// <remarks>
/// 這三個型別官方早就標成「未來版本會移除」，而且大多數字串函式對它們不適用——
/// 一個 <c>text</c> 資料行連 <c>LIKE</c> 之外的比較都做不了。
/// 嚴重度比 <see cref="SqlLegacyDateTimeRule"/> 高一級：那一條是「有更好的寫法」，
/// 這一條是「這個寫法有一天會消失」。
/// </remarks>
public sealed class SqlDeprecatedLargeObjectRule : ISqlSchemaRule
{
    public string Id => "SCHEMA-009";

    public string Title => "text／ntext／image 建議改用 max 型別";

    public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        foreach (var column in structure.Columns)
        {
            if (!SqlTypeName.IsDeprecatedLargeObject(column.DataType))
            {
                continue;
            }

            yield return new SqlSchemaFinding(
                Id,
                SqlSchemaSeverity.Warning,
                $"{column.DataType} 已經標成未來版本會移除，而且大多數字串函式對它不適用。",
                column.Name);
        }
    }
}
