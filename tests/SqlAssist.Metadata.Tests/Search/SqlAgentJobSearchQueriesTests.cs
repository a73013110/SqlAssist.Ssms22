using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 作業搜尋的查詢本身。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlCatalogSearchQueriesTests"/> 同一個理由：漏掉的東西會在本機執行成功
/// 並回傳本機的答案，畫面上看起來完全正常。那一組反射掃的是目錄那一份常數，
/// 掃不到這裡，所以同一條規則在這裡再掃一次。
/// </remarks>
public sealed class SqlAgentJobSearchQueriesTests
{
    /// <summary>加不了限定字的本機中繼資料函式；與 <c>SqlCatalogQualifierTests</c> 同一份名單。</summary>
    private static readonly Regex LocalMetadataFunction = new(
        @"\b(OBJECT_DEFINITION|OBJECT_NAME|OBJECT_SCHEMA_NAME|OBJECT_ID|OBJECTPROPERTY(EX)?" +
        @"|COLUMNPROPERTY|INDEXPROPERTY|INDEX_COL|SCHEMA_NAME|SCHEMA_ID|DB_NAME|DB_ID)\s*\(",
        RegexOptions.IgnoreCase);

    private static readonly Regex Parameter = new(@"@\w+");

    /// <summary>有人綁值的參數；其餘一律視為漏掉的。</summary>
    /// <remarks>
    /// 沒有人綁值的參數在執行期是「必須宣告純量變數」，而那是 DbException，
    /// 會被降級成「這個來源這一輪沒有資料」——症狀是作業搜尋安靜地空掉。
    /// 綁沒綁由 <see cref="FakeAgentCommand"/> 在每一次執行時當場檢查。
    /// </remarks>
    private static readonly HashSet<string> BoundParameters =
        new() { SqlAgentJobSearchQueries.JobIdParameterName };

    public static TheoryData<string, string> AllQueries()
    {
        var data = new TheoryData<string, string>();

        foreach (var field in Fields()) data.Add(field.Name, (string)field.GetValue(null)!);

        return data;
    }

    private static IEnumerable<FieldInfo> Fields() =>
        typeof(SqlAgentJobSearchQueries)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Where(field => ((string)field.GetValue(null)!).Contains("SELECT"));

    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 沒有用到加不了限定字的本機函式(string name, string query)
    {
        var found = LocalMetadataFunction.Match(query);

        Assert.False(found.Success, $"{name} 用了加不了限定字的 {found.Value}；改走目錄檢視。");
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

    /// <summary>
    /// 每一條都指名 <c>msdb</c>。
    /// </summary>
    /// <remarks>
    /// 少了限定字的話，查詢會在<b>使用者目前連的那個資料庫</b>裡找 <c>dbo.sysjobs</c>——
    /// 多半是「無效的物件名稱」，而那是 DbException，會被降級成「這個來源沒有資料」。
    /// 症狀不是錯誤，是作業搜尋只有在查詢視窗剛好連著 msdb 時才有結果。
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 每一條都指名msdb(string name, string query)
    {
        Assert.True(
            query.Contains("msdb.dbo.sysjobs"),
            $"{name} 沒有指名 msdb；作業是伺服器層級的，不跟著目前的資料庫走。");
    }

    /// <summary>
    /// 命令本文<b>不</b>跟識別同一條查詢。
    /// </summary>
    /// <remarks>
    /// 併成一條 <c>LEFT JOIN</c> 的話，不搜本文的那一輪省不掉任何東西：伺服器仍然要讀、
    /// 網路仍然要傳，而那一份會在讀取端被丟掉。
    /// </remarks>
    [Fact]
    public void 命令本文自成一條查詢()
    {
        Assert.DoesNotContain("s.command", SqlAgentJobSearchQueries.Jobs);
        Assert.Contains("s.command", SqlAgentJobSearchQueries.StepCommands);
    }

    /// <summary>
    /// 識別那一條走 <c>LEFT JOIN</c>，命令那一條走 <c>INNER JOIN</c>。
    /// </summary>
    /// <remarks>
    /// 前者是為了讓還沒有步驟的作業仍然搜得到名稱；後者是為了讓兩段看到的作業是同一組
    /// ——<c>sysjobs</c> 照登入過濾列，<c>sysjobsteps</c> 不會自己跟著濾，少了那一道，
    /// 權限受限的登入會拿到一批對不上任何作業的命令本文。
    /// </remarks>
    [Fact]
    public void 兩段的連接方式各有理由()
    {
        Assert.Contains("LEFT JOIN msdb.dbo.sysjobsteps", SqlAgentJobSearchQueries.Jobs);
        Assert.Contains("INNER JOIN msdb.dbo.sysjobs", SqlAgentJobSearchQueries.StepCommands);
        Assert.Contains("INNER JOIN msdb.dbo.sysjobs", SqlAgentJobSearchQueries.JobSteps);
    }

    /// <summary>
    /// <c>enabled</c> 在伺服器端就換成 <c>bit</c>。
    /// </summary>
    /// <remarks>
    /// 它是 <c>tinyint</c>；直接 <c>GetBoolean</c> 讀會拿到 InvalidCastException，
    /// 而那不是 DbException，降級接不住。
    /// </remarks>
    [Fact]
    public void 啟用旗標轉成bit再讀()
    {
        Assert.Contains("CAST(j.enabled AS bit)", SqlAgentJobSearchQueries.Jobs);
    }

    /// <summary>每一條都有固定的順序，同一台伺服器每次撈出來的先後一樣。</summary>
    /// <remarks>少了它，同分的結果先後由伺服器決定，清單會自己跳。</remarks>
    [Theory]
    [MemberData(nameof(AllQueries))]
    public void 每一條都有固定順序(string name, string query)
    {
        Assert.True(query.Contains("ORDER BY"), $"{name} 少了 ORDER BY。");
    }
}
