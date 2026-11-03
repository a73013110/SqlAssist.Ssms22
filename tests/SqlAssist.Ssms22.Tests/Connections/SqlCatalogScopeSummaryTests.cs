using System;
using SqlAssist.Core.Connections;
using SqlAssist.Core.Localization;
using SqlAssist.Ssms22.Connections;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Connections;

/// <summary>範圍列兩顆按鈕的摘要：SQL Search 與資料字典同一份寫法。</summary>
public sealed class SqlCatalogScopeSummaryTests
{
    [Fact]
    public void 多選資料庫沒勾是全部_一個寫名字_多個寫數量()
    {
        var scope = new SqlConnectionScope(multipleServers: false, StringComparer.OrdinalIgnoreCase);

        // 伺服器單選，所以摘要永遠是一個名字；還沒選時說「未選擇」。
        Assert.Equal(ConnectionText.NotSelected, SqlCatalogScopeSummary.Server(scope));
        // 資料庫沒勾就是「全部」，與面板第一列同一句。
        Assert.Equal(SqlCatalogScopeSummary.AllDatabasesLabel, SqlCatalogScopeSummary.Databases(scope));

        scope.SetServerSelected("LIBSQL01", selected: true);
        Assert.Equal("LIBSQL01", SqlCatalogScopeSummary.Server(scope));

        scope.SetDatabaseSelected("LibArchive", selected: true);
        Assert.Equal("LibArchive", SqlCatalogScopeSummary.Databases(scope));
        scope.SetDatabaseSelected("LibReporting", selected: true);
        Assert.Equal("2 個", SqlCatalogScopeSummary.Databases(scope));
    }

    /// <summary>單選必選的資料庫沒選時說「未選擇」，不說「全部」：資料字典一次只看一個資料庫。</summary>
    [Fact]
    public void 單選資料庫沒選是未選擇()
    {
        var scope = new SqlConnectionScope(multipleServers: false, StringComparer.OrdinalIgnoreCase, multipleDatabases: false);
        scope.SetServerSelected("LIBSQL01", selected: true);

        Assert.Equal(ConnectionText.NotSelected, SqlCatalogScopeSummary.Databases(scope));

        scope.SetDatabaseSelected("Library", selected: true);
        Assert.Equal("Library", SqlCatalogScopeSummary.Databases(scope));
    }

    [Fact]
    public void 英文介面跟著語言()
    {
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            var scope = new SqlConnectionScope(multipleServers: false, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Not selected", SqlCatalogScopeSummary.Server(scope));
            Assert.Equal("All", SqlCatalogScopeSummary.Databases(scope));
        }
    }
}
