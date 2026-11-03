using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Matching;
using SqlAssist.Metadata.Dictionary;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

/// <summary>主從與資料字典共用的一條規則：物件名稱或資料行名稱命中，再疊上兩個「只看」。</summary>
public sealed class SqlDictionaryFilterTests
{
    private static readonly SqlDataDictionary Dictionary = LibraryDictionary.Create();

    private static readonly SqlColumnNameGroups Groups = SqlColumnNameGroups.Build(Dictionary, LibraryDictionary.Loaded);

    /// <summary>每個物件留下的資料行，沒有任何一個命中的物件不列。</summary>
    private static Dictionary<string, string[]> Apply(SqlDictionaryFilter filter) =>
        Dictionary.Objects
            .Select(item => (item, columns: filter.MatchingColumns(item, LibraryDictionary.Loaded, Groups)))
            .Where(entry => entry.columns.Count > 0)
            .ToDictionary(entry => entry.item.DisplayName, entry => entry.columns.Select(column => column.Name).ToArray());

    /// <summary>剛打開時不含檢視；打開「含檢視」之後才列出來。</summary>
    [Fact]
    public void 預設不含檢視_其餘每一列都留著()
    {
        var result = Apply(SqlDictionaryFilter.Default);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain("rpt.LoanByReader", result.Keys);
        Assert.Equal(new[] { "LoanNo", "ReaderNo", "LoanDate" }, result["dbo.Loan"]);
        Assert.Equal(4, Apply(new SqlDictionaryFilter(includeViews: true)).Count);
    }

    /// <summary>打資料行名稱：主清單只剩有那個資料行的物件，明細只剩那幾個；不分大小寫。</summary>
    [Fact]
    public void 資料行名稱命中只留那幾個資料行()
    {
        var result = Apply(new SqlDictionaryFilter("readerno", includeViews: true));

        Assert.Equal(new[] { "dbo.Lib_Reader", "dbo.Loan", "rpt.LoanByReader" }, result.Keys);
        Assert.Equal(new[] { "ReaderNo" }, result["dbo.Loan"]);
        Assert.Equal(new[] { "READERNO" }, result["rpt.LoanByReader"]);
    }

    /// <summary>打物件名稱：命中的物件整張列出；只勾資料行時物件名稱不算。</summary>
    [Fact]
    public void 物件名稱命中列出整張表()
    {
        var result = Apply(new SqlDictionaryFilter("LoanDetail"));
        Assert.Equal(new[] { "LoanNo", "CopyNo" }, Assert.Single(result).Value);

        Assert.Empty(Apply(new SqlDictionaryFilter("LoanDetail", targets: SqlDictionaryMatchTargets.Columns)));
        Assert.Equal(
            new[] { "dbo.Loan", "dbo.LoanDetail" },
            Apply(new SqlDictionaryFilter("LoanNo", targets: SqlDictionaryMatchTargets.Columns)).Keys);
    }

    /// <summary>空白或逗號分開的幾個字任一命中就算；全字比對照 <see cref="TextMatcher"/>。</summary>
    [Fact]
    public void 幾個字任一命中_全字比對()
    {
        var result = Apply(new SqlDictionaryFilter("CopyNo, LoanDate", targets: SqlDictionaryMatchTargets.Columns));
        Assert.Equal(new[] { "dbo.Loan", "dbo.LoanDetail" }, result.Keys);

        var whole = Apply(new SqlDictionaryFilter("Loan", TextMatchOptions.WholeWord, SqlDictionaryMatchTargets.Objects));
        Assert.Equal(new[] { "dbo.Loan" }, whole.Keys);
    }

    [Fact]
    public void 只看沒說明_只看不一致()
    {
        var missing = Apply(new SqlDictionaryFilter(onlyMissingDescriptions: true));
        Assert.Equal(new[] { "LoanNo", "ReaderNo" }, missing["dbo.Loan"]);
        Assert.DoesNotContain("dbo.Lib_Reader", missing.Keys);

        var inconsistent = Apply(new SqlDictionaryFilter(onlyInconsistent: true));
        Assert.All(inconsistent.Values, names => Assert.All(names, name => Assert.Equal("readerno", name.ToLowerInvariant())));
    }
}
