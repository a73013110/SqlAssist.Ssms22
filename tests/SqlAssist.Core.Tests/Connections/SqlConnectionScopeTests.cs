using System;
using SqlAssist.Core.Connections;
using Xunit;

namespace SqlAssist.Core.Tests.Connections;

public sealed class SqlConnectionScopeTests
{
    /// <summary>
    /// 多選（SQL Memory）：伺服器與資料庫都是多選；換過伺服器就把資料庫一起清掉。
    /// </summary>
    /// <remarks>
    /// 資料庫名稱是每台伺服器自己的：留著上一輪的名單會篩成一列都沒有，
    /// 而畫面上只看得到「沒有符合條件」，看不出是上一台的條件還掛著。
    /// </remarks>
    [Fact]
    public void MultipleServersAreToggledAndDatabasesFollowTheServers()
    {
        var scope = new SqlConnectionScope(multipleServers: true, StringComparer.Ordinal);

        Assert.True(scope.SetServerSelected("LibraryServer", true));
        Assert.True(scope.SetServerSelected("ArchiveServer", true));
        // 勾第二次不是一次變更；呼叫端據此決定要不要重跑一輪。
        Assert.False(scope.SetServerSelected("ArchiveServer", true));
        Assert.Equal(new[] { "LibraryServer", "ArchiveServer" }, scope.Servers);
        Assert.True(scope.IsServerSelected("ArchiveServer"));
        Assert.False(scope.IsServerSelected("archiveserver"));

        Assert.True(scope.SetDatabaseSelected("Library", true));
        Assert.True(scope.SetDatabaseSelected("Archive", true));
        Assert.Equal(new[] { "Library", "Archive" }, scope.Databases);

        // 動到伺服器就把資料庫清掉，取消勾也一樣。
        Assert.True(scope.SetServerSelected("ArchiveServer", false));
        Assert.Equal(new[] { "LibraryServer" }, scope.Servers);
        Assert.Empty(scope.Databases);

        Assert.True(scope.SetDatabaseSelected("Library", true));
        Assert.True(scope.ClearDatabases());
        Assert.False(scope.ClearDatabases());
        Assert.Equal(new[] { "LibraryServer" }, scope.Servers);

        Assert.True(scope.SetDatabaseSelected("Library", true));
        Assert.True(scope.ClearServers());
        Assert.Empty(scope.Servers);
        Assert.Empty(scope.Databases);
        Assert.False(scope.ClearServers());

        Assert.Throws<ArgumentException>(() => scope.SetServerSelected("", true));
        Assert.Throws<ArgumentException>(() => scope.SetDatabaseSelected("", true));
    }

    /// <summary>單選（SQL Search）：勾一台就換掉上一台，取消勾不算數；資料庫照樣跟著清。</summary>
    [Fact]
    public void SingleServerIsReplacedAndCannotBeUnchecked()
    {
        var scope = new SqlConnectionScope(multipleServers: false, StringComparer.OrdinalIgnoreCase);

        Assert.True(scope.SetServerSelected("LibraryServer", true));
        Assert.True(scope.SetDatabaseSelected("Library", true));
        // 資料庫名稱的大小寫由定序決定；同一個資料庫不勾兩次。
        Assert.False(scope.SetDatabaseSelected("LIBRARY", true));

        Assert.False(scope.SetServerSelected("LibraryServer", false));
        Assert.False(scope.SetServerSelected("libraryserver", true));
        Assert.Equal(new[] { "Library" }, scope.Databases);

        Assert.True(scope.SetServerSelected("ArchiveServer", true));
        Assert.Equal(new[] { "ArchiveServer" }, scope.Servers);
        Assert.Empty(scope.Databases);
    }

    [Fact]
    public void ApplyingAConnectionReplacesBothFilters()
    {
        var scope = new SqlConnectionScope(multipleServers: true, StringComparer.Ordinal);
        scope.SetServerSelected("ArchiveServer", true);
        scope.SetServerSelected("LibraryServer", true);

        Assert.False(scope.Apply(null));
        Assert.False(scope.Apply(new SqlConnectionLabel("LibraryServer", "")));
        Assert.Equal(new[] { "ArchiveServer", "LibraryServer" }, scope.Servers);

        // 取代而不是加進去：這顆按鈕說的是「只看我現在連的那一個」。
        Assert.True(scope.Apply(new SqlConnectionLabel("LibraryServer", "Library")));
        Assert.Equal(new[] { "LibraryServer" }, scope.Servers);
        Assert.Equal(new[] { "Library" }, scope.Databases);
    }
}
