using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlConnectionNamesTests
{
    /// <summary>
    /// 名單去空白、去重並排好；三份請求共用同一份正規化。
    /// </summary>
    /// <remarks>
    /// 各自去重的話重複的名稱會進 <c>IN</c>，而游標指紋又是照名單原樣組的——同一組條件會因為
    /// 使用者勾選的先後算出兩個指紋，續頁那一刻被當成換過條件，整份清單跳回第一頁。
    /// </remarks>
    [Fact]
    public void NamesAreTrimmedDeduplicatedAndOrdered()
    {
        Assert.Empty(SqlConnectionNames.Normalize(null));
        Assert.Empty(SqlConnectionNames.Normalize(new[] { "", "  ", null! }));

        var names = SqlConnectionNames.Normalize(new[] { " LibraryServer ", "ArchiveServer", "LibraryServer" });
        Assert.Equal(new[] { "ArchiveServer", "LibraryServer" }, names);

        // 大小寫不同就是不同的名稱：儲存層以 = 精確比對，這裡放寬會讓條件配不到任何一列。
        Assert.Equal(new[] { "LibraryServer", "libraryserver" },
            SqlConnectionNames.Normalize(new[] { "libraryserver", "LibraryServer" }));
    }

    /// <summary>單一名稱也走同一條多值的路；儲存層不必認得兩種形狀。</summary>
    [Fact]
    public void OneWrapsASingleNameAndBlankMeansAny()
    {
        Assert.Empty(SqlConnectionNames.One(null));
        Assert.Empty(SqlConnectionNames.One(" "));
        Assert.Equal(new[] { "Library" }, SqlConnectionNames.One(" Library "));
    }

    /// <summary>指紋與勾選順序無關，而空名單與「沒有指定」算出同一個。</summary>
    [Fact]
    public void FingerprintIgnoresSelectionOrderAndTreatsEmptyAsUnset()
    {
        var first = SqlConnectionNames.Normalize(new[] { "ArchiveServer", "LibraryServer" });
        var second = SqlConnectionNames.Normalize(new[] { "LibraryServer", "ArchiveServer" });
        Assert.Equal(SqlConnectionNames.Fingerprint(first), SqlConnectionNames.Fingerprint(second));
        Assert.NotEqual(SqlConnectionNames.Fingerprint(first),
            SqlConnectionNames.Fingerprint(SqlConnectionNames.One("LibraryServer")));
        Assert.Equal("", SqlConnectionNames.Fingerprint(Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => SqlConnectionNames.Fingerprint(null!));
    }
}
