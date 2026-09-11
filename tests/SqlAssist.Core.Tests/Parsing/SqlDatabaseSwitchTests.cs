using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlDatabaseSwitchTests
{
    private static string? Find(string sql)
    {
        return SqlDatabaseSwitch.FindLast(sql, sql.Length);
    }

    [Fact]
    public void 讀出敘述開頭的資料庫()
    {
        Assert.Equal("LibArchive", Find("USE LibArchive"));
    }

    [Fact]
    public void 方括號名稱去掉括號()
    {
        Assert.Equal("Lib Archive", Find("USE [Lib Archive];"));
    }

    /// <summary>算數的是最後一個：前面那幾個早就被它蓋過去了。</summary>
    [Fact]
    public void 取最後一個()
    {
        Assert.Equal(
            "LibMirror",
            Find("USE LibArchive\nGO\nSELECT 1\nGO\nUSE LibMirror\nGO\n"));
    }

    /// <summary>游標之後的 USE 還沒輪到，不能拿來當現在的資料庫。</summary>
    [Fact]
    public void 只看指定位置之前()
    {
        const string sql = "USE LibArchive\nGO\nSELECT 1\nGO\nUSE LibMirror";

        Assert.Equal("LibArchive", SqlDatabaseSwitch.FindLast(sql, sql.IndexOf("SELECT 1")));
    }

    /// <summary>USE 前面不一定有分號或 GO。</summary>
    [Fact]
    public void 不需要敘述分隔符號()
    {
        Assert.Equal("LibArchive", Find("SET NOCOUNT ON\nUSE LibArchive\n"));
    }

    [Fact]
    public void 註解與字串裡的不算()
    {
        Assert.Null(Find("-- USE LibArchive\nSELECT 'USE LibMirror'\n"));
    }

    /// <summary>OPTION (USE PLAN N'…') 的 PLAN 是關鍵字，不是資料庫名稱。</summary>
    [Fact]
    public void 查詢提示不算()
    {
        Assert.Null(Find("SELECT 1 OPTION (USE PLAN N'<x/>')"));
    }

    [Fact]
    public void 加了引號的關鍵字仍是資料庫名稱()
    {
        Assert.Equal("PLAN", Find("USE [PLAN]"));
    }

    [Fact]
    public void 還沒打出名稱時沒有答案()
    {
        Assert.Null(Find("USE "));
    }

    [Fact]
    public void 沒有USE時沒有答案()
    {
        Assert.Null(Find("SELECT * FROM dbo.Lib_Reader"));
    }
}
