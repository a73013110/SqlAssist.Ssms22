using System.Text.RegularExpressions;
using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

/// <summary>資料字典的查詢本身：一輪兩段、不用本機中繼資料函式、vector 走舊版退路。</summary>
public sealed class SqlDataDictionaryQueriesTests
{
    /// <summary>與 <c>SqlCatalogQualifierTests</c> 同一份名單。</summary>
    private static readonly Regex LocalMetadataFunction = new(
        @"\b(OBJECT_DEFINITION|OBJECT_NAME|OBJECT_SCHEMA_NAME|OBJECT_ID|OBJECTPROPERTY(EX)?" +
        @"|COLUMNPROPERTY|INDEXPROPERTY|INDEX_COL|SCHEMA_NAME|SCHEMA_ID|DB_NAME|DB_ID)\s*\(",
        RegexOptions.IgnoreCase);

    [Fact]
    public void 一輪兩段且不用本機中繼資料函式()
    {
        Assert.Equal(SqlDataDictionaryQueries.Objects + SqlDataDictionaryQueries.Columns, SqlDataDictionaryQueries.Load);
        Assert.DoesNotMatch(LocalMetadataFunction, SqlDataDictionaryQueries.Load);
    }

    /// <summary>
    /// vector 那兩欄不直接 SELECT：SQL Server 2025 之前是語法錯誤，整份字典會降級成一列都沒有。
    /// </summary>
    [Fact]
    public void Vector欄位走舊版也編譯得過的退路()
    {
        Assert.Contains("AS no_vector", SqlDataDictionaryQueries.Columns, System.StringComparison.Ordinal);
        // 內層一加限定字，名稱解析就不會往外層找，退路斷掉；外層的 vec. 是 APPLY 的結果，不算。
        Assert.DoesNotMatch(new Regex(@"\b[cv]\.vector_"), SqlDataDictionaryQueries.Columns);
    }
}
