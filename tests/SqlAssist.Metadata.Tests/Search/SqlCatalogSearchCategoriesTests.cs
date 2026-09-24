using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Search;
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
    [InlineData("TR", "catalog.trigger")]
    [InlineData("TA", "catalog.trigger")]
    public void 型別代碼對到穩定的分類字串(string type, string expected)
    {
        Assert.Equal(expected, SqlCatalogSearchCategories.IdFor(SqlObjectKinds.FromSysObjectType(type)));
    }

    /// <summary>條件約束四種共用一顆 pill。</summary>
    /// <remarks>
    /// 分成四種的話，過濾列上會多出三顆幾乎沒有人會單獨勾的東西，而使用者問的是
    /// 「哪一條規則提到這個欄位」，不是「這是 CHECK 還是 DEFAULT」。
    /// </remarks>
    [Theory]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("PK")]
    [InlineData("UQ")]
    [InlineData("F")]
    public void 條件約束四種共用一個分類(string type)
    {
        Assert.Equal(SqlObjectKind.Constraint, SqlObjectKinds.FromSysObjectType(type));
        Assert.Equal(
            SqlCatalogSearchCategories.ConstraintCategoryId,
            SqlCatalogSearchCategories.IdFor(SqlObjectKinds.FromSysObjectType(type)));
    }

    /// <summary>同義字、序列與資料表型別各自一個分類，不併成收納桶。</summary>
    /// <remarks>併在一起的症狀是只要同義字的人篩不出來：勾那一個就三種一起來。</remarks>
    [Theory]
    [InlineData("SN", "catalog.synonym")]
    [InlineData("SO", "catalog.sequence")]
    [InlineData("TT", "catalog.table-type")]
    public void 少見的三種各自一個分類(string type, string expected)
    {
        Assert.Equal(expected, SqlCatalogSearchCategories.IdFor(SqlObjectKinds.FromSysObjectType(type)));
    }

    /// <summary>
    /// 認不得的型別代碼沒有分類，呼叫端整筆跳過。
    /// </summary>
    /// <remarks>給它一個泛用分類的話，使用者會看到一組點下去也沒有東西可以打開的結果。</remarks>
    [Theory]
    [InlineData("")]
    [InlineData("ZZ")]
    [InlineData("SQ")]
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

    /// <summary>資料行<b>不是</b>一種分類；它是另一條軸上的值。</summary>
    /// <remarks>
    /// 做成分類的症狀是勾「只看資料表」時，資料表上的資料行命中整組消失——
    /// 而那一勾要的正是它。
    /// </remarks>
    [Fact]
    public void 資料行不是一種分類()
    {
        var ids = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId)
            .Select(category => category.Id)
            .ToArray();

        Assert.DoesNotContain("catalog.column", ids);
    }

    [Fact]
    public void 分類清單涵蓋每一種進索引的物件()
    {
        var categories = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId);

        Assert.All(categories, category =>
            Assert.Equal(SqlCatalogSearchProvider.ProviderId, category.ProviderId));

        // Id 重複會讓過濾與去重失去唯一依據，而症狀是勾一個分類關掉兩種物件。
        Assert.Equal(categories.Count, categories.Select(category => category.Id).Distinct(StringComparer.Ordinal).Count());

        var ids = new HashSet<string>(categories.Select(category => category.Id), StringComparer.Ordinal);
        Assert.Contains(SqlCatalogSearchCategories.ConstraintCategoryId, ids);

        foreach (var kind in IndexedKinds())
        {
            Assert.Contains(SqlCatalogSearchCategories.IdFor(kind)!, ids);
        }
    }

    /// <summary>目錄物件的分類全在同一群，順序就是宣告順序。</summary>
    [Fact]
    public void 目錄物件的分類同一群且依宣告排序()
    {
        var categories = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId);

        Assert.All(categories, category => Assert.Equal(SqlCatalogSearchCategories.ObjectGroupId, category.GroupId));
        Assert.Equal(
            categories.Select(category => category.SortOrder).OrderBy(order => order),
            categories.Select(category => category.SortOrder));
        Assert.Equal("catalog.synonym", categories[categories.Count - 3].Id);
    }

    /// <summary>排序值不重複，否則兩顆 pill 的先後又回到宣告順序決定。</summary>
    [Fact]
    public void 排序值不重複()
    {
        var categories = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId);

        Assert.Equal(
            categories.Count,
            categories.Select(category => category.SortOrder).Distinct().Count());
    }

    /// <summary>顯示字與其他表面共用 <see cref="SqlObjectKinds.ToDisplayName"/>，不另外寫一份。</summary>
    [Fact]
    public void 顯示字沿用種類自己的名字()
    {
        var categories = SqlCatalogSearchCategories.Create(SqlCatalogSearchProvider.ProviderId);
        var table = Assert.Single(categories, category => category.Id == "catalog.table");
        var constraint = Assert.Single(
            categories, category => category.Id == SqlCatalogSearchCategories.ConstraintCategoryId);

        Assert.Equal(SqlObjectKind.Table.ToDisplayName(), table.DisplayName);
        Assert.Equal(SqlObjectKind.Constraint.ToDisplayName(), constraint.DisplayName);
    }

    /// <summary>分類與命中部位是兩條獨立的軸，不共用型別也不互相推導。</summary>
    [Fact]
    public void 分類與命中部位是兩條軸()
    {
        Assert.Equal(
            new[] { SearchMatchTarget.Name, SearchMatchTarget.Text, SearchMatchTarget.Column },
            Enum.GetValues(typeof(SearchMatchTarget)).Cast<SearchMatchTarget>());
    }

    private static IEnumerable<SqlObjectKind> IndexedKinds()
    {
        yield return SqlObjectKind.Table;
        yield return SqlObjectKind.View;
        yield return SqlObjectKind.Procedure;
        yield return SqlObjectKind.ScalarFunction;
        yield return SqlObjectKind.InlineTableFunction;
        yield return SqlObjectKind.TableValuedFunction;
        yield return SqlObjectKind.Trigger;
        yield return SqlObjectKind.Constraint;
        yield return SqlObjectKind.Synonym;
        yield return SqlObjectKind.Sequence;
        yield return SqlObjectKind.TableType;
    }
}
