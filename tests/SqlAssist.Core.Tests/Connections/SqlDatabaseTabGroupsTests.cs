using System.Linq;
using SqlAssist.Core.Connections;
using Xunit;

namespace SqlAssist.Core.Tests.Connections;

public sealed class SqlDatabaseTabGroupsTests
{
    /// <summary>SSMS 拿編號對 16 色取餘數；連續編號才保證前 16 個資料庫各是一色。</summary>
    [Fact]
    public void FirstSixteenDatabasesLandOnDistinctColors()
    {
        var groups = SqlDatabaseTabGroups.Parse(null);

        var ids = Enumerable.Range(0, 16)
            .Select(index => groups.IdOf(new SqlConnectionLabel("LibraryServer", $"Library{index}"), out _))
            .ToArray();

        Assert.Equal(16, ids.Select(id => id % 16).Distinct().Count());
    }

    [Fact]
    public void SameDatabaseKeepsItsGroupRegardlessOfCase()
    {
        var groups = SqlDatabaseTabGroups.Parse(null);

        var first = groups.IdOf(new SqlConnectionLabel("LibraryServer", "Library"), out var added);
        Assert.True(added);

        var again = groups.IdOf(new SqlConnectionLabel("libraryserver", "LIBRARY"), out added);
        Assert.False(added);
        Assert.Equal(first, again);
    }

    /// <summary>同名資料庫在另一台伺服器上不是同一個：測試機與正式機要看得出差別。</summary>
    [Fact]
    public void SameDatabaseNameOnAnotherServerIsAnotherGroup()
    {
        var groups = SqlDatabaseTabGroups.Parse(null);

        var library = groups.IdOf(new SqlConnectionLabel("LibraryServer", "Library"), out _);
        var archive = groups.IdOf(new SqlConnectionLabel("ArchiveServer", "Library"), out _);

        Assert.NotEqual(library, archive);
    }

    /// <summary>重開 SSMS 後每個資料庫要維持同一色，新的資料庫接在後面。</summary>
    [Fact]
    public void GroupsSurviveARoundTrip()
    {
        var groups = SqlDatabaseTabGroups.Parse(null);
        var library = groups.IdOf(new SqlConnectionLabel("LibraryServer", "Library"), out _);
        var archive = groups.IdOf(new SqlConnectionLabel("ArchiveServer", "Archive"), out _);

        var restored = SqlDatabaseTabGroups.Parse(groups.Format());

        Assert.Equal(library, restored.IdOf(new SqlConnectionLabel("LibraryServer", "Library"), out var added));
        Assert.False(added);
        Assert.Equal(archive, restored.IdOf(new SqlConnectionLabel("ArchiveServer", "Archive"), out added));
        Assert.False(added);
        Assert.Equal(2, restored.IdOf(new SqlConnectionLabel("LibraryServer", "Branch"), out added));
        Assert.True(added);
    }

    [Fact]
    public void DisplayNameSkipsTheMissingPart()
    {
        var groups = SqlDatabaseTabGroups.Parse(null);

        var both = groups.IdOf(new SqlConnectionLabel("LibraryServer", "Library"), out _);
        var serverOnly = groups.IdOf(new SqlConnectionLabel("ArchiveServer", string.Empty), out _);

        Assert.Equal("LibraryServer · Library", groups.DisplayName(both));
        Assert.Equal("ArchiveServer", groups.DisplayName(serverOnly));
        Assert.Equal(string.Empty, groups.DisplayName(99));
    }
}
