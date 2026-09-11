using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Querying;

/// <summary>
/// 一份目錄要打到哪裡去問。
/// </summary>
/// <remarks>
/// 漏掉一條查詢的限定字沒有徵兆：它會在<b>本機</b>執行成功並回傳本機的東西，
/// 而畫面上看起來完全正常。因此這裡不逐條列舉，而是把
/// <see cref="SqlMetadataQueries"/> 的每一條都掃過一遍。
/// </remarks>
public sealed class SqlCatalogQualifierTests
{
    /// <summary>沒有被限定字前置的目錄檢視參考。</summary>
    private static readonly Regex UnqualifiedCatalogView = new(@"(?<!\]\.)\bsys\.");

    /// <summary>加不了限定字的本機中繼資料函式。</summary>
    /// <remarks>
    /// 這一族吃的是 object_id／column_id，而它們在<b>執行這句話的那個資料庫</b>裡
    /// 解析。跨資料庫時連線已經換過去了所以沒事；跨連結伺服器時整句在對方執行，
    /// 目錄檢視被 <c>[db].sys.</c> 換到了對的資料庫，這一族卻仍然在對方登入的
    /// 預設資料庫裡找——多半得到 NULL，運氣不好時得到另一個剛好同號的物件的答案。
    /// </remarks>
    private static readonly Regex LocalMetadataFunction = new(
        @"\b(OBJECT_DEFINITION|OBJECT_NAME|OBJECT_SCHEMA_NAME|OBJECT_ID|OBJECTPROPERTY(EX)?" +
        @"|COLUMNPROPERTY|INDEXPROPERTY|INDEX_COL|SCHEMA_NAME|SCHEMA_ID|DB_NAME|DB_ID)\s*\(",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// 允許用本機函式的查詢，以及那一次為什麼非用不可。
    /// </summary>
    /// <remarks>
    /// 用名單而不是全面禁止：有些東西目錄檢視上根本沒有，只問得到這一族。
    /// 名單存在的意義是讓下一次新增變成一個要寫理由的決定，而不是順手加一個
    /// <c>OBJECT_DEFINITION</c>——那一條的降級沒有任何徵兆。
    /// </remarks>
    private static readonly Dictionary<string, string> LocalFunctionsAllowed = new()
    {
        [nameof(SqlMetadataQueries.Columns)] =
            "GeneratedAlwaysType、IsSparse、IsRowGuidCol 走 COLUMNPROPERTY，" +
            "因為 sys.columns.generated_always_type 要 SQL Server 2016 才有。",
        [nameof(SqlMetadataQueries.SystemColumns)] =
            "與 Columns 是同一份本體，只換掉資料行的目錄檢視；理由同上。",
        [nameof(SqlMetadataQueries.TableStorage)] =
            "QUOTED_IDENTIFIER 不在任何目錄檢視上，只問得到 OBJECTPROPERTY。",
        [nameof(SqlMetadataQueries.DatabaseCollation)] =
            "問的是「目前這個資料庫」，而指出它的方式只有 DB_NAME／DB_ID；" +
            "sys.databases 要先知道名字，那個名字正是這條要問的東西。" +
            "這一條只在本機目錄上執行——連結伺服器的目錄一律不問定序，" +
            "見 SqlMetadataCatalog.GetCollationsAsync。"
    };

    public static TheoryData<string, string> AllQueries()
    {
        var data = new TheoryData<string, string>();

        foreach (var field in Fields())
        {
            data.Add(field.Name, (string)field.GetValue(null)!);
        }

        return data;
    }

    private static IEnumerable<FieldInfo> Fields()
    {
        return typeof(SqlMetadataQueries)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string) &&
                            field.Name != nameof(SqlMetadataQueries.ObjectIdParameterName));
    }

    /// <summary>
    /// 本機中繼資料函式只出現在名單上的那幾條。
    /// </summary>
    /// <remarks>
    /// 這一條與限定字那一條是同一個症狀的兩半：漏掉的東西會在本機執行成功並
    /// 回傳本機的答案，畫面上看不出退過。<c>OBJECT_DEFINITION</c> 已經因此換成
    /// <c>sys.sql_modules</c> 兩次（模組定義與觸發程序），這裡把第三次擋在測試上。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 本機中繼資料函式只出現在說得出理由的查詢裡(string name, string query)
    {
        if (LocalFunctionsAllowed.ContainsKey(name))
        {
            return;
        }

        var found = LocalMetadataFunction.Match(query);

        Assert.False(
            found.Success,
            $"{name} 用了加不了限定字的 {found.Value}；" +
            "改走目錄檢視，真的沒有別的來源時把理由寫進 LocalFunctionsAllowed。");
    }

    [Fact]
    public void 本機的查詢一個字都不改()
    {
        Assert.Equal(
            SqlMetadataQueries.Schemas,
            SqlCatalogQualifier.Local.Compose(SqlMetadataQueries.Schemas));
    }

    /// <remarks>
    /// 這一條是整個型別存在的理由。漏掉的那條查詢會在本機執行成功，
    /// 回傳目前這個資料庫的東西當成對面那台伺服器的答案。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 遠端的查詢沒有一個目錄檢視漏掉限定字(string name, string query)
    {
        var composed = SqlCatalogQualifier
            .ForLinkedServer("LibMirror", "LibArchive")
            .Compose(query, objectId: 42);

        Assert.False(
            UnqualifiedCatalogView.IsMatch(composed),
            $"{name} 有沒有加上限定字的 sys. 參考：{composed}");
    }

    /// <remarks>
    /// <c>OPENQUERY</c> 的內層是字串常值，參數傳不進去。漏掉的症狀是執行期的
    /// 「必須宣告純量變數 @objectId」，而降級會把它變成「這一層安靜地空掉」。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 遠端的查詢不留參數名稱(string name, string query)
    {
        var composed = SqlCatalogQualifier
            .ForLinkedServer("LibMirror", "LibArchive")
            .Compose(query, objectId: 42);

        Assert.DoesNotContain(SqlMetadataQueries.ObjectIdParameterName, composed);
        Assert.StartsWith("SELECT * FROM OPENQUERY([LibMirror], '", composed);
        Assert.NotEqual(string.Empty, name);
    }

    /// <remarks>
    /// 查詢裡本來就有單引號（<c>type IN ('U', 'V')</c>）。沒有加倍的話整句
    /// 在第一個引號就斷掉，變成語法錯誤而不是「查不到」。
    /// </remarks>
    [Fact]
    public void 內層的單引號一律加倍()
    {
        var composed = SqlCatalogQualifier
            .ForLinkedServer("LibMirror", "LibArchive")
            .Compose(SqlMetadataQueries.Objects);

        Assert.Contains("''U''", composed);
        Assert.DoesNotContain("'U'", composed.Replace("''U''", string.Empty));
    }

    [Fact]
    public void 字串常值裡的sys不會被誤換()
    {
        var composed = SqlCatalogQualifier
            .ForLinkedServer("LibMirror", "LibArchive")
            .Compose(SqlMetadataQueries.Schemas);

        // 排除的是名為 sys 的結構描述，那是一個字串常值，不是目錄檢視的參考。
        Assert.Contains("''sys''", composed);
    }

    /// <remarks>
    /// object_id 的格式必須是不變文化：跟著地區設定跑的話，某些地區會寫出
    /// 帶群組分隔符號的數字，而整句會變成語法錯誤。
    /// </remarks>
    [Fact]
    public void 物件識別碼內嵌成不帶分隔符號的常值()
    {
        var composed = SqlCatalogQualifier
            .ForLinkedServer("LibMirror", "LibArchive")
            .Compose(SqlMetadataQueries.Columns, objectId: 1234567);

        Assert.Contains("= 1234567", composed);
    }

    /// <remarks>
    /// <c>LibMirror.</c> 這一格只問得到資料庫清單，而 <c>sys.databases</c>
    /// 是伺服器層級的，在對方登入的預設資料庫裡問就對了——加上資料庫限定字
    /// 反而要先知道一個我們還沒問到的名字。
    /// </remarks>
    [Fact]
    public void 伺服器本身那一格不加資料庫限定字()
    {
        var qualifier = SqlCatalogQualifier.ForLinkedServer("LibMirror");

        Assert.True(qualifier.IsServerRoot);
        Assert.Contains("FROM sys.databases", qualifier.Compose(SqlMetadataQueries.Databases));
    }

    [Fact]
    public void 以位址命名的伺服器加得上方括號()
    {
        var composed = SqlCatalogQualifier
            .ForLinkedServer("192.0.2.10", "LibArchive")
            .Compose(SqlMetadataQueries.Schemas);

        Assert.StartsWith("SELECT * FROM OPENQUERY([192.0.2.10], '", composed);
    }

    [Fact]
    public void 名稱為空時不建立限定字()
    {
        Assert.Throws<ArgumentException>(() => SqlCatalogQualifier.ForLinkedServer(" "));
        Assert.Throws<ArgumentException>(() => SqlCatalogQualifier.ForLinkedServer("LibMirror", " "));
    }
}
