using System;
using System.Linq;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Formatting;

public sealed class TSqlScriptRendererRegressionTests
{
    [Fact]
    public void 觸發程序前後都必須切開批次()
    {
        const string definition = "CREATE TRIGGER dbo.TR_Loan ON dbo.Loan AFTER INSERT AS SELECT 1;";
        var structure = new SqlObjectStructure(Detail(), triggers: new[] { new SqlTriggerInfo("TR_Loan", definition) });
        var script = structure.BuildScript(Context(SqlScriptOptions.Minimal with { IncludeTriggers = true }));
        Assert.Equal(definition, Assert.Single(Batches(script), batch => batch.Contains("CREATE TRIGGER")));
    }

    [Fact]
    public void 第二層預覽載入期間不能複製出缺索引的DDL()
    {
        var structure = new SqlObjectStructure(Detail(), structurePending: true);
        Assert.False(structure.CanBuildExecutableScript);
        var script = structure.BuildScript(Context(SqlScriptOptions.Fidelity));
        Assert.Empty(SqlTokenizer.Tokenize(script));
        Assert.Contains("載入中", script);
        Assert.DoesNotContain("查詢失敗", script);
    }

    [Theory]
    [InlineData(SqlObjectKind.Procedure, "CREATE PROCEDURE dbo.Loan AS SELECT 1;")]
    [InlineData(SqlObjectKind.View, "CREATE VIEW dbo.Loan AS SELECT 1 AS LoanId;")]
    [InlineData(SqlObjectKind.ScalarFunction, "CREATE FUNCTION dbo.Loan() RETURNS int AS BEGIN RETURN 1; END;")]
    public void 模組不與前面的SET或後面的資料表共用批次(SqlObjectKind kind, string definition)
    {
        var module = new SqlObjectStructure(new SqlObjectDetail(new SqlObjectInfo(1, "dbo", "Loan", kind), definition: definition));
        var script = TSqlScriptRenderer.Default.Render(new[] { module, new SqlObjectStructure(Detail()) },
            Context(SqlScriptOptions.Minimal with { SetOptions = SqlSetOptionOutput.AlwaysOn }));
        Assert.Equal(definition, Assert.Single(Batches(script), batch => batch.Contains(definition)));
    }

    [Theory]
    [InlineData("computed")]
    [InlineData("check")]
    [InlineData("default")]
    public void 運算式缺失時不交出可執行的半份結構(string missing)
    {
        var detail = missing == "computed"
            ? Detail(new SqlColumnInfo(1, "LoanId", "int", false, isComputed: true))
            : missing == "default"
                ? Detail(new SqlColumnInfo(1, "LoanId", "int", false,
                    script: new SqlColumnScriptDetail("int", 4, 10, 0, defaultConstraintName: "DF_Loan")))
                : Detail();
        var structure = new SqlObjectStructure(detail,
            checkConstraints: missing == "check" ? new[] { new SqlCheckConstraint("CK_Loan", "") } : null);
        Assert.False(structure.CanBuildExecutableScript);
        Assert.Empty(SqlTokenizer.Tokenize(structure.BuildScript(Context(SqlScriptOptions.Fidelity))));
    }

    [Fact]
    public void 多行檔頭與不可用摘要的每一行都是註解()
    {
        var context = new SqlScriptContext(SqlScriptOptions.Fidelity with { IncludeHeaderComment = true },
            newLine: "\n", databaseName: "Library\r\nSELECT 999;", serverName: "Library\rSELECT 998;");
        Assert.Empty(SqlTokenizer.Tokenize(TSqlScriptRenderer.Default.Render(Array.Empty<SqlObjectStructure>(), context)));

        var structure = new SqlObjectStructure(new SqlObjectDetail(
            new SqlObjectInfo(1, "dbo", "Loan\nSELECT 997;", SqlObjectKind.Table),
            new[] { new SqlColumnInfo(1, "LoanId", "int", false, isComputed: true, computedDefinition: "(1\n+2)") }),
            structureUnavailable: true);
        Assert.Empty(SqlTokenizer.Tokenize(structure.BuildScript(Context(SqlScriptOptions.Fidelity))));
    }

    [Theory]
    [InlineData(SqlConstraintNaming.Never)]
    [InlineData(SqlConstraintNaming.OnlyUserNamed)]
    public void 停用CHECK必須保留後續停用所需的名稱(SqlConstraintNaming naming)
    {
        var structure = new SqlObjectStructure(Detail(), checkConstraints: new[]
        {
            new SqlCheckConstraint("CK_Loan", "([LoanId]>(0))", isDisabled: true, isSystemNamed: true)
        });
        var script = structure.BuildScript(Context(SqlScriptOptions.Fidelity with { ConstraintNaming = naming }));
        Assert.Contains("ADD CONSTRAINT [CK_Loan] CHECK", script);
        Assert.Contains("NOCHECK CONSTRAINT [CK_Loan]", script);
    }

    [Fact]
    public void 未命名CHECK不以舊名稱假裝提供冪等判斷()
    {
        var structure = new SqlObjectStructure(Detail(), checkConstraints: new[]
        {
            new SqlCheckConstraint("CK_Loan", "([LoanId]>(0))", isSystemNamed: true)
        });
        var script = structure.BuildScript(Context(SqlScriptOptions.Minimal with { ExistenceCheck = SqlExistenceCheck.IfNotExists }));
        Assert.DoesNotContain("CK_Loan", script);
    }

    [Fact]
    public void 省略附屬物件時不寫入指向它的擴充屬性()
    {
        var structure = new SqlObjectStructure(Detail(),
            indexes: new[] { new SqlIndexInfo(2, "IX_Loan", new[] { new SqlIndexColumn("LoanId", false, false) }) },
            extendedProperties: new[] { new SqlExtendedProperty(SqlExtendedPropertyLevel.Index, "MS_Description", "借閱", "IX_Loan") });
        var script = structure.BuildScript(Context(SqlScriptOptions.Fidelity with { IncludeIndexes = false }));
        Assert.DoesNotContain("EXEC sp_addextendedproperty", script);
        Assert.Contains("--", script);
    }

    [Fact]
    public void 省略系統DEFAULT名稱時也不寫入舊名稱的說明()
    {
        var column = new SqlColumnInfo(1, "LoanId", "int", false, defaultDefinition: "(0)",
            script: new SqlColumnScriptDetail("int", 4, 10, 0,
                defaultConstraintName: "DF_Loan_System", defaultIsSystemNamed: true));
        var structure = new SqlObjectStructure(Detail(column), extendedProperties: new[]
        {
            new SqlExtendedProperty(SqlExtendedPropertyLevel.Constraint, "MS_Description", "預設值", "DF_Loan_System"),
            new SqlExtendedProperty(SqlExtendedPropertyLevel.Column, "MS_Description", "借閱編號", "LoanId")
        });
        var script = structure.BuildScript(Context(SqlScriptOptions.Minimal with { IncludeExtendedProperties = true }));
        var commands = script.Split('\n').Where(line => line.StartsWith("EXEC ", StringComparison.Ordinal)).ToArray();
        Assert.Contains("'COLUMN', N'LoanId'", Assert.Single(commands));
    }

    [Fact]
    public void 約束存在性判斷不拆含點號的已跳脫名稱()
    {
        var structure = new SqlObjectStructure(new SqlObjectDetail(
            new SqlObjectInfo(1, "dbo", "Loan.Detail", SqlObjectKind.Table), Detail().Columns),
            checkConstraints: new[] { new SqlCheckConstraint("CK.Loan", "([LoanId]>(0))") });
        var script = structure.BuildScript(Context(SqlScriptOptions.Fidelity with { ExistenceCheck = SqlExistenceCheck.IfNotExists }));
        Assert.Contains("parent_object_id = OBJECT_ID(N'[dbo].[Loan.Detail]') AND name = N'CK.Loan'", script);
    }

    [Fact]
    public void 檔頭加SET不影響可編輯指令碼的名稱落點()
    {
        var structure = new SqlObjectStructure(new SqlObjectDetail(
            new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Procedure), definition: "CREATE PROCEDURE dbo.Loan AS SELECT 1;"));
        var script = SqlObjectScript.BuildEditable(structure, Context(SqlScriptOptions.Fidelity with
        {
            IncludeHeaderComment = true, SetOptions = SqlSetOptionOutput.AlwaysOn, ModuleStatement = SqlModuleStatement.Alter
        }));
        Assert.Equal(script.Text.IndexOf("dbo.Loan", StringComparison.Ordinal) + "dbo.Loan".Length, script.CaretOffset);
    }

    private static SqlObjectDetail Detail(params SqlColumnInfo[] columns) => new(
        new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
        columns.Length == 0 ? new[] { new SqlColumnInfo(1, "LoanId", "int", false) } : columns);

    private static SqlScriptContext Context(SqlScriptOptions options) => new(options, newLine: "\n");
    private static string[] Batches(string script) => script.Split(new[] { "\nGO\n" }, StringSplitOptions.RemoveEmptyEntries)
        .Select(batch => batch.Trim()).Where(batch => batch.Length > 0).ToArray();
}
