using System.Text.RegularExpressions;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Querying;

public sealed class SqlMetadataQueriesTests
{
    /// <summary>加了限定字的 vector 欄位參考；<c>vec.</c> 是 APPLY 的結果，不算。</summary>
    private static readonly Regex QualifiedVectorColumn = new(@"\b(?!vec\.)\w+\.vector_");

    /// <remarks>
    /// vector 那兩欄要 SQL Server 2025 才有，靠的是子查詢裡不加限定字的欄位找不到時
    /// 往外層的 NULL 綁。內層一寫成 <c>v.vector_dimensions</c>，舊版整份欄位查詢就是
    /// 語法錯誤，而降級會讓欄位建議、<c>SELECT *</c> 展開與結構預覽一起安靜地消失。
    /// </remarks>
    [Theory]
    [InlineData(nameof(SqlMetadataQueries.Columns), SqlMetadataQueries.Columns)]
    [InlineData(nameof(SqlMetadataQueries.SystemColumns), SqlMetadataQueries.SystemColumns)]
    [InlineData(nameof(SqlMetadataQueries.Parameters), SqlMetadataQueries.Parameters)]
    [InlineData(nameof(SqlMetadataQueries.SystemParameters), SqlMetadataQueries.SystemParameters)]
    public void vector欄位只從退路讀(string name, string query)
    {
        Assert.Contains("AS no_vector", query);
        Assert.False(
            QualifiedVectorColumn.IsMatch(query),
            $"{name} 直接讀了 SQL Server 2025 才有的欄位：{QualifiedVectorColumn.Match(query).Value}");
    }
}
