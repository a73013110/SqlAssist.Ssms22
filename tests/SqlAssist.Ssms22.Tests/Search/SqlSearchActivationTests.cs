using System;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Search;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

/// <summary>
/// 一筆結果屬於哪一台，由酬載自己說；接線層不照目前的範圍代答。
/// </summary>
public sealed class SqlSearchActivationTests
{
    private static readonly SqlSearchOrigin Origin = new("LIBSQL02");

    [Fact]
    public void 目錄物件交出搜到它的那一台()
    {
        var hit = Hit(new SqlCatalogSearchTarget(
            Origin, "Library", "dbo", "Cat_BookCopy", SqlObjectKind.Constraint, 42));

        Assert.Same(Origin, SqlSearchActivation.OriginOf(hit));
    }

    [Fact]
    public void 作業交出連線那一台而不是自報的名字()
    {
        var hit = Hit(new SqlAgentJobSearchTarget(
            Origin, "LIBSQL01", Guid.Empty, "Lib_Loan 夜間維護", isEnabled: true));

        Assert.Same(Origin, SqlSearchActivation.OriginOf(hit));
    }

    /// <summary>不是伺服器上的東西就沒有伺服器，不回一個猜的。</summary>
    [Fact]
    public void 不是伺服器上的東西沒有伺服器()
    {
        Assert.Null(SqlSearchActivation.OriginOf(null));
        Assert.Null(SqlSearchActivation.OriginOf(Hit("片段")));
        Assert.Null(SqlSearchActivation.OriginOf(Hit(null)));
    }

    private static SearchHit Hit(object? payload) =>
        new("catalog", "catalog.table", SearchMatchTarget.Name, "Cat_BookCopy", "Cat_BookCopy", 10,
            activatePayload: payload);
}
