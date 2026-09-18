using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using Xunit;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// <c>sys.objects</c> 的型別代碼到搜尋分類的對應。
/// </summary>
/// <remarks>
/// 這一組釘住的是<b>字串</b>而不是列舉：分類 Id 會被寫進使用者偏好，跨版本更名的症狀是
/// 使用者下次打開搜尋時勾的是另一種物件，而畫面上完全看不出來。
/// </remarks>
public sealed class SqlCatalogSearchCategoriesTests
{
    [Theory]
    [InlineData("U", "catalog.table")]
    [InlineData("V", "catalog.view")]
    [InlineData("P", "catalog.procedure")]
    [InlineData("PC", "catalog.procedure")]
    [InlineData("FN", "catalog.scalar-function")]
    [InlineData("FS", "catalog.scalar-function")]
    [InlineData("IF", "catalog.inline-table-function")]
    [InlineData("TF", "catalog.table-valued-function")]
    [InlineData("FT", "catalog.table-valued-function")]
    [InlineData("SN", "catalog.synonym")]
    [InlineData("TR", "catalog.trigger")]
    [InlineData("TA", "catalog.trigger")]
    [InlineData("SO", "catalog.sequence")]
    [InlineData("TT", "catalog.table-type")]
    public void 型別代碼對到穩定的分類字串(string type, string expected)
    {
        Assert.Equal(expected, SqlCatalogSearchCategories.IdFor(SqlObjectKinds.FromSysObjectType(type)));
    }

    /// <summary>
    /// 認不得的型別代碼沒有分類，呼叫端整筆跳過。
    /// </summary>
    /// <remarks>
    /// 給它一個「其他」分類的話，使用者會看到一組勾不掉的結果，而那些列點下去
    /// 也沒有東西可以打開——目前這條路徑根本不知道那是什麼。
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("ZZ")]
    [InlineData("D")]
    public void 認不得的型別代碼沒有分類(string type)
    {
        Assert.Equal(SqlObjectKind.Unknown, SqlObjectKinds.FromSysObjectType(type));
        Assert.Null(SqlCatalogSearchCategories.IdFor(SqlObjectKinds.FromSysObjectType(type)));
    }

    /// <summary>指令碼自己宣告的三種一列都不在 <c>sys.objects</c> 上，這個來源掃不到。</summary>
    [Theory]
    [InlineData(SqlObjectKind.TemporaryTable)]
    [InlineData(SqlObjectKind.TableVariable)]
    [InlineData(SqlObjectKind.CommonTableExpression)]
    public void 指令碼宣告的種類沒有分類(SqlObjectKind kind)
    {
        Assert.Null(SqlCatalogSearchCategories.IdFor(kind));
    }

    [Fact]
    public void 分類清單涵蓋每一種進索引的物件再加上資料行()
    {
        var categories = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId);

        Assert.All(categories, category =>
            Assert.Equal(SqlCatalogSearchProvider.ProviderId, category.ProviderId));

        // Id 重複會讓過濾與去重失去唯一依據，而症狀是勾一個分類關掉兩種物件。
        Assert.Equal(categories.Count, categories.Select(category => category.Id).Distinct(StringComparer.Ordinal).Count());

        var ids = new HashSet<string>(categories.Select(category => category.Id), StringComparer.Ordinal);
        Assert.Contains(SqlCatalogSearchCategories.ColumnCategoryId, ids);

        foreach (var kind in IndexedKinds())
        {
            Assert.Contains(SqlCatalogSearchCategories.IdFor(kind)!, ids);
        }
    }

    /// <summary>顯示字與其他表面共用 <see cref="SqlObjectKinds.ToDisplayName"/>，不另外寫一份。</summary>
    [Fact]
    public void 顯示字沿用種類自己的名字()
    {
        var categories = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId);
        var table = Assert.Single(categories, category => category.Id == "catalog.table");

        Assert.Equal(SqlObjectKind.Table.ToDisplayName(), table.DisplayName);
    }

    private static IEnumerable<SqlObjectKind> IndexedKinds()
    {
        yield return SqlObjectKind.Table;
        yield return SqlObjectKind.View;
        yield return SqlObjectKind.Procedure;
        yield return SqlObjectKind.ScalarFunction;
        yield return SqlObjectKind.InlineTableFunction;
        yield return SqlObjectKind.TableValuedFunction;
        yield return SqlObjectKind.Synonym;
        yield return SqlObjectKind.Trigger;
        yield return SqlObjectKind.Sequence;
        yield return SqlObjectKind.TableType;
    }
}
