using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 搜尋索引的查詢本身。
/// </summary>
/// <remarks>
/// 與 <c>SqlCatalogQualifierTests</c> 同一個理由：漏掉的東西會在本機執行成功並回傳
/// 本機的答案，畫面上看起來完全正常。查詢是另一份常數（索引要的欄位與第一層不同），
/// 那一組反射掃不到這裡，所以同一條規則在這裡再掃一次。
/// </remarks>
public sealed class SqlCatalogSearchQueriesTests
{
    /// <summary>加不了限定字的本機中繼資料函式；與 <c>SqlCatalogQualifierTests</c> 同一份名單。</summary>
    /// <remarks>
    /// 這一族吃的是 object_id／column_id，而它們在<b>執行這句話的那個資料庫</b>裡解析。
    /// 跨資料庫時連線已經換過去了所以沒事；之後要支援連結伺服器時，目錄檢視會被
    /// <c>[db].sys.</c> 換到對的資料庫，這一族卻仍然在對方登入的預設資料庫裡找。
    /// 全量索引沒有任何一條非用它不可，所以這裡是全面禁止，沒有豁免名單。
    /// </remarks>
    private static readonly Regex LocalMetadataFunction = new(
        @"\b(OBJECT_DEFINITION|OBJECT_NAME|OBJECT_SCHEMA_NAME|OBJECT_ID|OBJECTPROPERTY(EX)?" +
        @"|COLUMNPROPERTY|INDEXPROPERTY|INDEX_COL|SCHEMA_NAME|SCHEMA_ID|DB_NAME|DB_ID)\s*\(",
        RegexOptions.IgnoreCase);

    public static TheoryData<string, string> AllQueries()
    {
        var data = new TheoryData<string, string>();

        foreach (var field in Fields())
        {
            data.Add(field.Name, (string)field.GetValue(null)!);
        }

        return data;
    }

    private static IEnumerable<FieldInfo> Fields() =>
        typeof(SqlCatalogSearchQueries)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string));

    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 沒有用到加不了限定字的本機函式(string name, string query)
    {
        var found = LocalMetadataFunction.Match(query);

        Assert.False(
            found.Success,
            $"{name} 用了加不了限定字的 {found.Value}；改走目錄檢視。");
    }

    /// <remarks>
    /// v1 是整份重建，一條參數都不吃。留著參數名稱而沒有人綁值的症狀是執行期的
    /// 「必須宣告純量變數」，而那是 <c>DbException</c>，會被降級成「這一輪沒有資料」
    /// ——搜尋對那個資料庫安靜地空掉。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 沒有留下沒有人綁值的參數(string name, string query)
    {
        Assert.False(query.Contains('@'), $"{name} 留下了沒有人綁值的參數。");
    }

    /// <summary>掃全庫的兩條查詢一定要限制在使用者物件上。</summary>
    /// <remarks>
    /// 漏掉 <c>is_ms_shipped = 0</c> 的症狀不是錯誤而是噪音：每一個資料庫多出一兩千個
    /// 系統物件的名稱與定義本文，索引大小翻倍，而搜尋結果第一頁全是使用者沒寫過的東西。
    /// </remarks>
    [Fact]
    public void 物件查詢只收使用者物件()
    {
        Assert.Contains("is_ms_shipped = 0", SqlCatalogSearchQueries.ObjectsWithDefinitions);
        Assert.Contains("tt.is_user_defined = 1", SqlCatalogSearchQueries.ObjectsWithDefinitions);
    }

    /// <summary>定義本文與物件同一次來回；逐物件問一次是明文禁止的。</summary>
    /// <remarks>
    /// 幾千個物件就是幾千次來回，而使用者按的只是一次搜尋。
    /// </remarks>
    [Fact]
    public void 定義本文與物件同一次來回()
    {
        Assert.Contains("LEFT JOIN sys.sql_modules", SqlCatalogSearchQueries.ObjectsWithDefinitions);
    }
}
