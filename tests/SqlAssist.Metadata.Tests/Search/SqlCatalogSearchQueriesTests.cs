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

    /// <summary>查詢裡出現的參數。</summary>
    private static readonly Regex Parameter = new(@"@\w+");

    /// <summary>
    /// 有人綁值的參數；其餘一律視為漏掉的。
    /// </summary>
    /// <remarks>
    /// 名單而不是「一個都不准有」：增量重新整理非要一個界線值不可，而把時間直接寫進 SQL
    /// 字面值會踩到 <c>datetime</c> 與 <c>datetime2</c> 的精確度與地區設定。真正要擋的是
    /// <b>沒有人綁值</b>的參數——那在執行期是「必須宣告純量變數」，而那是 DbException，
    /// 會被降級成「這一輪沒有資料」，搜尋對那個資料庫安靜地空掉。
    /// 綁沒綁由 <see cref="FakeCatalogCommand"/> 在每一次執行時當場檢查。
    /// </remarks>
    private static readonly HashSet<string> BoundParameters =
        new() { SqlCatalogSearchQueries.ModifiedAfterParameterName };

    public static TheoryData<string, string> AllQueries()
    {
        var data = new TheoryData<string, string>();

        foreach (var field in Fields())
        {
            data.Add(field.Name, (string)field.GetValue(null)!);
        }

        return data;
    }

    /// <remarks>
    /// 以「內容含 SELECT」認查詢，而不是把每一個公開字串欄位都當成查詢：這個型別上還有
    /// 參數名稱那種常數，拿它去比對本機函式與參數規則只會得到兩條沒有意義的斷言。
    /// </remarks>
    private static IEnumerable<FieldInfo> Fields() =>
        typeof(SqlCatalogSearchQueries)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Where(field => ((string)field.GetValue(null)!).Contains("SELECT"));

    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 沒有用到加不了限定字的本機函式(string name, string query)
    {
        var found = LocalMetadataFunction.Match(query);

        Assert.False(
            found.Success,
            $"{name} 用了加不了限定字的 {found.Value}；改走目錄檢視。");
    }

    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 每一個參數都有人綁值(string name, string query)
    {
        foreach (Match match in Parameter.Matches(query))
        {
            Assert.True(
                BoundParameters.Contains(match.Value),
                $"{name} 留下了沒有人綁值的參數 {match.Value}。");
        }
    }

    /// <summary>掃全庫的查詢一定要限制在使用者物件上。</summary>
    /// <remarks>
    /// 漏掉 <c>is_ms_shipped = 0</c> 的症狀不是錯誤而是噪音：每一個資料庫多出一兩千個
    /// 系統物件的名稱與定義本文，索引大小翻倍，而搜尋結果第一頁全是使用者沒寫過的東西。
    /// </remarks>
    [Theory]
    [InlineData(nameof(SqlCatalogSearchQueries.Objects))]
    [InlineData(nameof(SqlCatalogSearchQueries.Definitions))]
    [InlineData(nameof(SqlCatalogSearchQueries.Columns))]
    public void 只收使用者物件(string name)
    {
        var query = (string)typeof(SqlCatalogSearchQueries).GetField(name)!.GetValue(null)!;

        Assert.Contains("is_ms_shipped = 0", query);
    }

    [Fact]
    public void 物件查詢也擋掉系統定義的資料表型別()
    {
        Assert.Contains("tt.is_user_defined = 1", SqlCatalogSearchQueries.Objects);
    }

    /// <summary>
    /// 定義本文<b>不</b>跟物件同一條查詢。
    /// </summary>
    /// <remarks>
    /// 併成一條 <c>LEFT JOIN sys.sql_modules</c> 的話，不搜本文的那一輪省不掉任何東西：
    /// 伺服器仍然要讀、網路仍然要傳，而那一份會在讀取端被丟掉。分兩段之後，
    /// <c>Targets</c> 少掉 <c>Text</c> 的那一輪連送都不送。
    /// </remarks>
    [Fact]
    public void 定義本文自成一條查詢()
    {
        Assert.DoesNotContain("sys.sql_modules", SqlCatalogSearchQueries.Objects);
        Assert.Contains("sys.sql_modules", SqlCatalogSearchQueries.Definitions);
    }

    /// <summary>
    /// 條件約束四種一次 UNION 回來，而且結構描述接的是父物件的。
    /// </summary>
    /// <remarks>
    /// <c>sys.objects</c> 上的條件約束沒有自己的 <c>schema_id</c>（它跟著父物件走），
    /// 照物件那條 JOIN 會接到錯的結構描述，而畫面上那個結構描述看起來完全正常。
    /// </remarks>
    [Theory]
    [InlineData("sys.check_constraints")]
    [InlineData("sys.default_constraints")]
    [InlineData("sys.key_constraints")]
    [InlineData("sys.foreign_keys")]
    public void 條件約束四種都在物件查詢裡(string view)
    {
        Assert.Contains(view, SqlCatalogSearchQueries.Objects);
    }

    [Fact]
    public void 條件約束接父物件的結構描述()
    {
        Assert.Contains("parent_object_id", SqlCatalogSearchQueries.Objects);
    }

    /// <summary>
    /// 只有貴的那兩條走增量；物件那一條整份重撈。
    /// </summary>
    /// <remarks>
    /// 物件那一條是唯一看得出「哪一個被卸除了」的一條，而卸除不會留下時間戳。
    /// 它也走增量的話，被砍掉的那一張表會永遠留在索引上。
    /// </remarks>
    [Fact]
    public void 增量界線只加在資料行與定義本文上()
    {
        Assert.DoesNotContain(SqlCatalogSearchQueries.ModifiedAfterParameterName, SqlCatalogSearchQueries.Objects);
        Assert.Contains(SqlCatalogSearchQueries.ModifiedAfterParameterName, SqlCatalogSearchQueries.Definitions);
        Assert.Contains(SqlCatalogSearchQueries.ModifiedAfterParameterName, SqlCatalogSearchQueries.Columns);
    }

    /// <summary>資料庫清單只列進得去而且上線的。</summary>
    /// <remarks>
    /// 離線或進不去的資料庫列出來，只會讓使用者勾一個必然失敗的目標，而失敗在畫面上
    /// 與「這裡面沒有東西」長得一樣。
    /// </remarks>
    [Fact]
    public void 資料庫清單只列進得去而且上線的()
    {
        Assert.Contains("d.state = 0", SqlCatalogSearchQueries.Databases);
        Assert.Contains("HAS_DBACCESS", SqlCatalogSearchQueries.Databases);
    }
}
