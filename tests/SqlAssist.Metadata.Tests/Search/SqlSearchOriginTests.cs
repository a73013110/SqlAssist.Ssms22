using System;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 一筆結果的伺服器在建立那一刻就定下來，少了它就不准建立。
/// </summary>
/// <remarks>
/// 沒有伺服器的命中，下游只剩「照目前的範圍猜」這條退路，而範圍換過之後猜到的是另一台。
/// </remarks>
public sealed class SqlSearchOriginTests
{
    private static readonly SqlSearchOrigin Origin = new("LIBSQL01");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 說不出伺服器就不建立(string? serverName) =>
        Assert.Throws<ArgumentException>(() => new SqlSearchOrigin(serverName!));

    [Fact]
    public void 目錄命中少了伺服器就不建立() =>
        Assert.Throws<ArgumentNullException>(
            () => new SqlCatalogSearchTarget(null!, "Library", "dbo", "Loan", SqlObjectKind.Table, 1));

    [Fact]
    public void 作業命中少了伺服器就不建立() =>
        Assert.Throws<ArgumentNullException>(
            () => new SqlAgentJobSearchTarget(null!, "LIBSQL01", Guid.Empty, "Lib_Loan 夜間維護", isEnabled: true));

    /// <summary>兩種酬載從同一個介面交出伺服器；接線層不必逐一認得每一種。</summary>
    [Fact]
    public void 兩種酬載從同一個介面交出伺服器()
    {
        ISqlSearchTarget catalog = new SqlCatalogSearchTarget(
            Origin, "Library", "dbo", "Loan", SqlObjectKind.Table, 1);
        ISqlSearchTarget job = new SqlAgentJobSearchTarget(
            Origin, "LIBSQL01", Guid.Empty, "Lib_Loan 夜間維護", isEnabled: true);

        Assert.Same(Origin, catalog.Origin);
        Assert.Same(Origin, job.Origin);
    }
}
