using System.Linq;
using SqlAssist.Metadata.Analysis;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Analysis;

public sealed class SqlIndexRuleRegressionTests
{
    [Theory]
    [InlineData("filter")]
    [InlineData("include")]
    [InlineData("order")]
    [InlineData("disabled")]
    [InlineData("clustered")]
    public void 鍵前綴相同但用途不同不能宣稱可刪除(string difference)
    {
        var candidate = new SqlIndexInfo(1, "IX_Loan", new[]
        {
            new SqlIndexColumn("LoanId", false, false),
            new SqlIndexColumn("CopyNo", false, false),
            new SqlIndexColumn("Remark", false, true)
        }, typeDescription: difference == "clustered" ? "CLUSTERED" : "NONCLUSTERED");
        var otherColumns = new[]
        {
            new SqlIndexColumn("LoanId", false, false),
            new SqlIndexColumn("CopyNo", difference == "order", false),
            new SqlIndexColumn("BranchId", false, false),
            new SqlIndexColumn(difference == "include" ? "BranchName" : "Remark", false, true)
        };
        var other = new SqlIndexInfo(2, "IX_Loan_Other", otherColumns,
            filterDefinition: difference == "filter" ? "([LoanId]>(0))" : null,
            options: new SqlIndexOptions(isDisabled: difference == "disabled"));
        Assert.Empty(new SqlRedundantIndexRule().Analyze(Structure(candidate, other)));
    }

    [Fact]
    public void 真正重疊仍提示評估但不指示直接刪除()
    {
        var first = new SqlIndexInfo(1, "IX_Loan", new[] { new SqlIndexColumn("LoanId", false, false) });
        var second = new SqlIndexInfo(2, "IX_Loan_Copy", new[]
        {
            new SqlIndexColumn("LoanId", false, false), new SqlIndexColumn("CopyNo", false, false)
        });
        var finding = Assert.Single(new SqlRedundantIndexRule().Analyze(Structure(first, second)));
        Assert.DoesNotContain("可以刪掉", finding.Message);
    }

    [Fact]
    public void 完全相同的索引不會互相要求刪除()
    {
        var first = new SqlIndexInfo(1, "IX_Loan", new[] { new SqlIndexColumn("LoanId", false, false) });
        var second = new SqlIndexInfo(2, "IX_Loan_Copy", first.Columns);
        Assert.Single(new SqlRedundantIndexRule().Analyze(Structure(first, second)));
    }

    [Fact]
    public void 叢集資料行存放區不是堆積()
    {
        var index = new SqlIndexInfo(1, "CCI_Loan", new[] { new SqlIndexColumn("LoanId", false, false) },
            typeDescription: "CLUSTERED COLUMNSTORE");
        Assert.Empty(new SqlHeapTableRule().Analyze(Structure(index)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 結構尚未就緒不產生假缺鍵警告(bool pending)
    {
        var detail = Structure().Detail;
        var structure = new SqlObjectStructure(detail, structurePending: pending, structureUnavailable: !pending);
        Assert.Empty(SqlSchemaAnalyzer.Default.Analyze(structure));
    }

    private static SqlObjectStructure Structure(params SqlIndexInfo[] indexes) => new(
        new SqlObjectDetail(new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
            new[] { new SqlColumnInfo(1, "LoanId", "int", false) }), indexes);
}
