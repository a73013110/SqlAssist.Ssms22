using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 一個名稱在使用者物件裡找不到時，該不該退回問系統物件。
/// </summary>
/// <remarks>
/// 只驗證文字判斷，不碰快照也不碰資料庫——這一支的價值正是在
/// <see cref="SqlObjectLookup"/>、<c>SqlObjectLocator</c> 都還沒問中繼資料之前就能單獨測。
/// </remarks>
public sealed class SqlSystemObjectFallbackTests
{
    [Theory]
    [InlineData("sys")]
    [InlineData("Sys")]
    [InlineData("INFORMATION_SCHEMA")]
    [InlineData("information_schema")]
    public void 限定字是系統結構描述時直接用那個限定字(string qualifier)
    {
        Assert.True(SqlSystemObjectFallback.TryGetFallbackSchema(qualifier, "sp_helpindex", out var schema));
        Assert.Equal(qualifier, schema);
    }

    [Theory]
    [InlineData("dbo")]
    [InlineData("Library")]
    public void 限定字是使用者結構描述時不成立(string qualifier)
    {
        Assert.False(SqlSystemObjectFallback.TryGetFallbackSchema(qualifier, "sp_help", out _));
    }

    [Theory]
    [InlineData("sp_help")]
    [InlineData("SP_HELP")]
    [InlineData("xp_cmdshell")]
    [InlineData("Xp_Cmdshell")]
    public void 未限定但長得像系統程序時退回sys(string name)
    {
        Assert.True(SqlSystemObjectFallback.TryGetFallbackSchema(null, name, out var schema));
        Assert.Equal("sys", schema);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void 未限定但沒有限定字也沒有名稱時不成立(string? qualifier)
    {
        Assert.False(SqlSystemObjectFallback.TryGetFallbackSchema(qualifier, null, out _));
    }

    [Theory]
    [InlineData("Lib_Reader")]
    [InlineData("Loan")]
    [InlineData("sp")]
    [InlineData("xp")]
    public void 未限定但不像系統程序時不成立(string name)
    {
        Assert.False(SqlSystemObjectFallback.TryGetFallbackSchema(null, name, out _));
    }

    /// <summary>
    /// 限定字已經是使用者自己的結構描述時，即使名稱長得像系統程序也不套用未限定的退路——
    /// 那一條規則只在<b>完全沒有限定字</b>時才生效，判斷順序不能反過來。
    /// </summary>
    [Fact]
    public void 有使用者結構描述限定字時不套用未限定規則()
    {
        Assert.False(SqlSystemObjectFallback.TryGetFallbackSchema("dbo", "sp_help", out _));
    }
}
