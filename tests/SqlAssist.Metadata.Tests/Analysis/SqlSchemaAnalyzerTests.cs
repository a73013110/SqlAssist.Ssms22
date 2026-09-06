using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Metadata.Analysis;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Tests.Formatting;
using Xunit;

namespace SqlAssist.Metadata.Tests.Analysis;

/// <summary>
/// 健檢規則。
/// </summary>
/// <remarks>
/// 每一條規則各驗兩件事：抓得到該抓的，以及<b>不</b>抓不該抓的。只驗前者的話，
/// 一條「永遠回報有問題」的規則會全綠通過——而誤報正是使用者關掉整個健檢的原因。
/// </remarks>
public sealed class SqlSchemaAnalyzerTests
{
    private static IReadOnlyList<SqlSchemaFinding> AnalyzeLoan() =>
        SqlSchemaAnalyzer.Default.Analyze(LoanTableFixture.Create());

    private static IReadOnlyList<string> IdsFor(SqlObjectStructure structure) =>
        SqlSchemaAnalyzer.Default.Analyze(structure).Select(f => f.RuleId).Distinct().ToList();

    // ── 驗收：那張表該被抓到的幾件事 ──────────────────────────────────

    [Fact]
    public void 抓得到INCLUDE帶了大型物件的索引()
    {
        var finding = Assert.Single(AnalyzeLoan(), f => f.RuleId == "SCHEMA-001");

        Assert.Equal("IX_Loan_3", finding.TargetName);
        Assert.Contains("Remark", finding.Message);
        Assert.Equal(SqlSchemaSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void 抓得到同語意資料行的Unicode混用()
    {
        var findings = AnalyzeLoan().Where(f => f.RuleId == "SCHEMA-005").ToList();

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.TargetName!.StartsWith("CreateUser", StringComparison.Ordinal));
        Assert.All(findings, f => Assert.Contains("LoanUser", f.Message));
    }

    [Fact]
    public void 抓得到列舉語意卻沒有CHECK的資料行()
    {
        var finding = Assert.Single(AnalyzeLoan(), f => f.RuleId == "SCHEMA-006");

        Assert.Equal("Status", finding.TargetName);
    }

    [Fact]
    public void 抓得到每一個datetime資料行()
    {
        var findings = AnalyzeLoan().Where(f => f.RuleId == "SCHEMA-008").ToList();

        Assert.Equal(4, findings.Count);
        Assert.All(findings, f => Assert.Equal(SqlSchemaSeverity.Information, f.Severity));
    }

    // ── 誤報：那張表不該被抓到的幾件事 ────────────────────────────────

    /// <remarks>
    /// PublicId 以 Id 結尾，卻是這張表自己的候選鍵（有唯一索引）。
    /// 把它報成「疑似外來鍵」是使用者最先失去信任的那種誤報。
    /// </remarks>
    [Fact]
    public void 有唯一索引的資料行不算疑似外來鍵()
    {
        var findings = AnalyzeLoan().Where(f => f.RuleId == "SCHEMA-007").ToList();

        Assert.DoesNotContain(findings, f => f.TargetName == "PublicId");
        Assert.DoesNotContain(findings, f => f.TargetName == "LoanId");
        Assert.Contains(findings, f => f.TargetName == "BranchNo");
    }

    /// <remarks>
    /// LoanId int 與 PublicId uniqueidentifier 都以 Id 結尾，型別大類卻不同——
    /// 那個差異多半是刻意的，一個是流水號一個是對外的識別碼。
    /// </remarks>
    [Fact]
    public void 型別大類不同的同後綴資料行不報()
    {
        Assert.DoesNotContain(
            AnalyzeLoan().Where(f => f.RuleId == "SCHEMA-005"),
            f => f.TargetName!.StartsWith("PublicId", StringComparison.Ordinal));
    }

    [Fact]
    public void 有叢集索引也有主索引鍵的資料表不報那兩條()
    {
        var ids = AnalyzeLoan().Select(f => f.RuleId).ToList();

        Assert.DoesNotContain("SCHEMA-003", ids);
        Assert.DoesNotContain("SCHEMA-004", ids);
    }

    [Fact]
    public void 索引鍵沒有前綴重疊時不報多餘索引()
    {
        Assert.DoesNotContain("SCHEMA-002", AnalyzeLoan().Select(f => f.RuleId));
    }

    [Fact]
    public void 沒有淘汰型別時不報那一條()
    {
        Assert.DoesNotContain("SCHEMA-009", AnalyzeLoan().Select(f => f.RuleId));
    }

    // ── 各條規則的單獨情境 ────────────────────────────────────────────

    [Fact]
    public void 沒有主索引鍵的資料表同時報堆積與缺鍵()
    {
        var ids = IdsFor(Table(new[] { Column("TagId", "int") }));

        Assert.Contains("SCHEMA-003", ids);
        Assert.Contains("SCHEMA-004", ids);
    }

    /// <remarks>
    /// 資料行一列都沒有回來是「這一輪沒有資料」，不是這張表沒有主索引鍵。
    /// 報出來的話，每一次權限不足或物件剛被卸除都會多出兩則假的發現。
    /// </remarks>
    [Fact]
    public void 一個資料行都查不到時什麼都不報()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(1, "dbo", "Lib_Tag", SqlObjectKind.Table)));

        Assert.Empty(SqlSchemaAnalyzer.Default.Analyze(structure));
    }

    [Fact]
    public void 索引鍵是另一個索引前綴時報多餘()
    {
        var structure = Table(
            new[] { Column("BranchNo", "varchar(10)"), Column("LoanTime", "datetime2(3)") },
            new[]
            {
                Index(2, "IX_A", "BranchNo"),
                Index(3, "IX_B", "BranchNo", "LoanTime")
            });

        var finding = Assert.Single(
            SqlSchemaAnalyzer.Default.Analyze(structure), f => f.RuleId == "SCHEMA-002");

        Assert.Equal("IX_A", finding.TargetName);
        Assert.Contains("IX_B", finding.Message);
    }

    /// <remarks>主索引鍵與唯一索引同時是條件約束，刪不掉也不該刪。</remarks>
    [Fact]
    public void 主索引鍵是別的索引前綴時不報多餘()
    {
        var structure = Table(
            new[] { Column("LoanId", "int"), Column("LoanTime", "datetime2(3)") },
            new[]
            {
                new SqlIndexInfo(
                    1, "PK_Loan", new[] { new SqlIndexColumn("LoanId") },
                    isPrimaryKey: true, isUnique: true, typeDescription: "CLUSTERED"),
                Index(2, "IX_B", "LoanId", "LoanTime")
            });

        Assert.DoesNotContain("SCHEMA-002", IdsFor(structure));
    }

    [Fact]
    public void 有CHECK的列舉資料行不報()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Lib_Tag", SqlObjectKind.Table),
                new[] { Column("Status", "tinyint") }),
            extendedProperties: new[]
            {
                new SqlExtendedProperty(
                    SqlExtendedPropertyLevel.Column, "MS_Description", "狀態：1=在架, 2=出借", "Status")
            },
            checkConstraints: new[]
            {
                new SqlCheckConstraint("CK_Status", "([Status]>=(1) AND [Status]<=(2))")
            });

        Assert.DoesNotContain("SCHEMA-006", IdsFor(structure));
    }

    /// <remarks>沒有說明的資料行無從判斷是不是列舉，報出來就是猜。</remarks>
    [Fact]
    public void 沒有說明的小整數資料行不報列舉()
    {
        Assert.DoesNotContain("SCHEMA-006", IdsFor(Table(new[] { Column("Status", "tinyint") })));
    }

    [Fact]
    public void 有外來鍵的資料行不報疑似外來鍵()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
                new[] { Column("BranchNo", "varchar(10)") }),
            foreignKeys: new[]
            {
                new SqlForeignKeyInfo(
                    "FK_Loan_Branch", "dbo", "Branch",
                    new[] { new SqlForeignKeyColumn("BranchNo", "BranchNo") })
            });

        Assert.DoesNotContain("SCHEMA-007", IdsFor(structure));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("ntext")]
    [InlineData("image")]
    public void 淘汰的大型物件型別各自報一次(string dataType)
    {
        var finding = Assert.Single(
            SqlSchemaAnalyzer.Default.Analyze(Table(new[] { Column("Content", dataType) })),
            f => f.RuleId == "SCHEMA-009");

        Assert.Contains(dataType, finding.Message);
    }

    // ── 可插拔 ────────────────────────────────────────────────────────

    [Fact]
    public void 只跑指定的規則()
    {
        var analyzer = SqlSchemaAnalyzer.ForRules(new[] { "SCHEMA-008" });

        Assert.All(
            analyzer.Analyze(LoanTableFixture.Create()),
            f => Assert.Equal("SCHEMA-008", f.RuleId));
    }

    /// <remarks>
    /// 空集合是「一條都不跑」，與 null 的「全開」是兩回事——混為一談的話，
    /// 使用者關掉最後一條規則會得到全部打開。
    /// </remarks>
    [Fact]
    public void 空的規則清單一條都不跑()
    {
        Assert.Empty(SqlSchemaAnalyzer.ForRules(Array.Empty<string>()).Analyze(LoanTableFixture.Create()));
        Assert.NotEmpty(SqlSchemaAnalyzer.ForRules(null).Analyze(LoanTableFixture.Create()));
    }

    [Fact]
    public void 每一條內建規則的識別碼與標題都不重複()
    {
        var ids = SqlSchemaAnalyzer.BuiltInRules.Select(r => r.Id).ToList();
        var titles = SqlSchemaAnalyzer.BuiltInRules.Select(r => r.Title).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("SCHEMA-", id));
    }

    /// <remarks>
    /// 一條規則炸掉不可以讓整份指令碼變不出來：使用者要的是那份指令碼，
    /// 不是一則健檢錯誤。
    /// </remarks>
    [Fact]
    public void 一條規則擲例外時其餘照跑()
    {
        var analyzer = new SqlSchemaAnalyzer(
            new ISqlSchemaRule[] { new ThrowingRule(), new SqlLegacyDateTimeRule() });

        Assert.All(analyzer.Analyze(LoanTableFixture.Create()), f => Assert.Equal("SCHEMA-008", f.RuleId));
    }

    /// <summary>順序每次不同的話，兩份內容相同的指令碼會 diff 出一整片紅。</summary>
    [Fact]
    public void 同一份結構跑兩次的順序相同()
    {
        Assert.Equal(
            AnalyzeLoan().Select(f => f.Describe()),
            AnalyzeLoan().Select(f => f.Describe()));
    }

    [Fact]
    public void 註解文字帶得出規則識別碼與嚴重度()
    {
        var finding = new SqlSchemaFinding("SCHEMA-001", SqlSchemaSeverity.Warning, "訊息", "IX_Loan_3");

        Assert.Equal("[SCHEMA-001][Warning] IX_Loan_3：訊息", finding.Describe());
    }

    private sealed class ThrowingRule : ISqlSchemaRule
    {
        public string Id => "SCHEMA-999";

        public string Title => "永遠爆炸";

        public IEnumerable<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
        {
            yield return new SqlSchemaFinding(Id, SqlSchemaSeverity.Warning, "先給一則再爆炸");
            throw new InvalidOperationException("測試用。");
        }
    }

    private static SqlColumnInfo Column(string name, string dataType) =>
        new(1, name, dataType, isNullable: true);

    private static SqlObjectStructure Table(
        IReadOnlyList<SqlColumnInfo> columns,
        IReadOnlyList<SqlIndexInfo>? indexes = null) =>
        new(
            new SqlObjectDetail(new SqlObjectInfo(1, "dbo", "Lib_Tag", SqlObjectKind.Table), columns),
            indexes);

    private static SqlIndexInfo Index(int id, string name, params string[] columns) =>
        new(
            id,
            name,
            columns.Select(c => new SqlIndexColumn(c)).ToArray(),
            typeDescription: "NONCLUSTERED");
}
