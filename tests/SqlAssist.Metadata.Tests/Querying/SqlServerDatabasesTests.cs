using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Querying;

public sealed class SqlServerDatabasesTests
{
    /// <summary>資料庫清單標得出進不去而且離線的。</summary>
    /// <remarks>
    /// 離線或進不去的資料庫列進下拉，只會讓使用者選一個必然失敗的目標，而失敗在畫面上
    /// 與「這裡面沒有東西」長得一樣。
    /// </remarks>
    [Fact]
    public void 清單標得出進不去而且離線的()
    {
        Assert.Contains("d.state = 0", SqlServerDatabases.Query);
        Assert.Contains("HAS_DBACCESS", SqlServerDatabases.Query);
    }

    [Theory]
    [InlineData("master", true)]
    [InlineData("TempDB", true)]
    [InlineData("msdb", true)]
    [InlineData("LibArchive", false)]
    public void 系統資料庫的名稱不分大小寫(string name, bool expected) =>
        Assert.Equal(expected, SqlServerDatabase.IsSystemName(name));
}
