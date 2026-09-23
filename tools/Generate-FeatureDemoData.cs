#:project ../src/SqlAssist.Core/SqlAssist.Core.csproj
#:project ../src/SqlAssist.Metadata/SqlAssist.Metadata.csproj
#:property PublishAot=false

using System.Text;
using System.Text.Json;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Scripting;
using SqlAssist.Core.Snippets;
using SqlAssist.Core.Statements;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.ResultGrid;

// 動畫的 SQL 由產品產生器輸出；只提供虛構中繼資料，不連線、不執行 SQL。
var loan = new SqlObjectStructure(
    new SqlObjectDetail(new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table),
    [
        new SqlColumnInfo(1, "LoanId", "int", false, isPrimaryKey: true),
        new SqlColumnInfo(2, "CopyNo", "nvarchar(20)", false),
        new SqlColumnInfo(3, "ReaderId", "int", false)
    ]),
    indexes:
    [
        new SqlIndexInfo(1, "PK_Loan", [new SqlIndexColumn("LoanId")],
            isPrimaryKey: true, isUnique: true, typeDescription: "CLUSTERED"),
        new SqlIndexInfo(2, "IX_Loan_ReaderId", [new SqlIndexColumn("ReaderId")])
    ],
    foreignKeys:
    [
        new SqlForeignKeyInfo("FK_Loan_Lib_Reader", "dbo", "Lib_Reader",
            [new SqlForeignKeyColumn("ReaderId", "ReaderId")])
    ],
    extendedProperties:
    [
        new SqlExtendedProperty(SqlExtendedPropertyLevel.Table, "MS_Description", "圖書館借閱紀錄")
    ]);

// 與 SqlScriptPreferences.CreateForExecution 相同的三項強制覆寫。
var options = SqlScriptOptions.Fidelity with
{
    SetOptions = SqlSetOptionOutput.FromCatalog,
    BatchSeparation = SqlBatchSeparation.BetweenStatements,
    ModuleStatement = SqlModuleStatement.Alter,
    IncludeExtendedProperties = true,
    IncludeHeaderComment = false,
    IncludeAnalyzerComments = false
};
var definition = SqlObjectScript.BuildEditable(loan, new SqlScriptContext(options, newLine: "\n"));

const string selection = "SELECT ReaderId, ReaderName\nFROM dbo.Lib_Reader;";
if (!SqlSnippetDefaults.Current.TryGet("ifb", out var snippet) || !snippet.CanSurround)
    throw new InvalidOperationException("內建 ifb 片段不可包夾。");
var surround = SqlSnippetExpansion.Create(snippet, selection).Render("\n", "");
var candidates = SqlSnippetDefaults.Current.Snippets.Where(s => s.CanSurround)
    .Select(s => new { shortcut = s.Shortcut, title = s.Title, description = s.Description }).ToArray();

SqlColumnInfo[] tagColumns =
[
    new(1, "TagId", "int", false, isIdentity: true),
    new(2, "TagName", "nvarchar(80)", false),
    new(3, "CreatedAt", "datetime2", false, defaultDefinition: "(sysdatetime())"),
    new(4, "Description", "nvarchar(200)", true)
];
var insertColumns = tagColumns.Where(c => c.CanInsert)
    .Select(c => new SqlStatementColumn(c.Name, c.DataType, c.IsNullable,
        !string.IsNullOrEmpty(c.DefaultDefinition))).ToArray();
var insert = SqlInsertStatementText.Build("dbo.Lib_Tag", insertColumns, "", "\n", out var insertCaret);

const string procedureDefinition = """
    CREATE PROCEDURE dbo.usp_Loan_Count
        @ReaderId int,
        @MinLoanId int = 0,
        @LoanCount int OUTPUT
    AS
    BEGIN
        SET NOCOUNT ON;
        SELECT @LoanCount = COUNT(*)
        FROM dbo.Loan
        WHERE ReaderId = @ReaderId AND LoanId >= @MinLoanId;
    END
    """;
const string functionDefinition = """
    CREATE FUNCTION dbo.fn_LoanCount(@ReaderId int)
    RETURNS int
    AS
    BEGIN
        DECLARE @LoanCount int;
        SELECT @LoanCount = COUNT(*)
        FROM dbo.Loan
        WHERE ReaderId = @ReaderId;
        RETURN @LoanCount;
    END
    """;
var optionalParameters = SqlModuleParameterDefaults.Find(procedureDefinition);
SqlStatementParameter[] procedureParameters =
[
    new("@ReaderId", "int", false, optionalParameters.Contains("@ReaderId")),
    new("@MinLoanId", "int", false, optionalParameters.Contains("@MinLoanId")),
    new("@LoanCount", "int", true, optionalParameters.Contains("@LoanCount"))
];
var execute = SqlProcedureCallText.Build("EXEC", "dbo.usp_Loan_Count", procedureParameters, "", "\n",
    out var executeCaret);
var merge = SqlMergeStatementText.Build("dbo.Cat_BookCopy", ["CopyNo"], ["CopyNo", "BranchId"],
    "", "\n", out var mergeCaret);
if (!SqlModuleScript.TryConvertCreateToAlter(procedureDefinition, out var alterProcedure)
    || !SqlModuleScript.TryConvertCreateToAlter(functionDefinition, out var alterFunction))
    throw new InvalidOperationException("示範模組定義無法轉為 ALTER。");
var gridValues = new[] { "B001", "B002", "B002", "B003" };
var predicate = SqlInPredicateScript.Build(new ResultGridTable(
    [new ResultGridColumn("CopyNo", "nvarchar(20)")],
    gridValues.Take(3).Select(value => new object?[] { value }).ToArray(), false));

if (!definition.Text.Contains("FK_Loan_Lib_Reader", StringComparison.Ordinal)
    || !definition.Text.Contains("IX_Loan_ReaderId", StringComparison.Ordinal)
    || !definition.Text.Contains("sp_addextendedproperty", StringComparison.Ordinal)
    || insert.Contains("TagId", StringComparison.Ordinal)
    || !insert.Contains("DEFAULT", StringComparison.Ordinal)
    || !optionalParameters.SetEquals(["@MinLoanId"])
    || !execute.Contains("@LoanCount = @LoanCount OUTPUT", StringComparison.Ordinal)
    || !merge.Contains("WHEN MATCHED AND 1 = 0", StringComparison.Ordinal)
    || !merge.Contains("WHEN NOT MATCHED BY TARGET AND 1 = 0", StringComparison.Ordinal)
    || !alterProcedure.StartsWith("ALTER PROCEDURE dbo.usp_Loan_Count", StringComparison.Ordinal)
    || !alterFunction.StartsWith("ALTER FUNCTION dbo.fn_LoanCount", StringComparison.Ordinal)
    || SqlModuleScript.FindHeaderNameEnd(alterProcedure) < 0
    || SqlModuleScript.FindHeaderNameEnd(alterFunction) < 0
    || !predicate.Contains("B002", StringComparison.Ordinal)
    || predicate.Contains("B003", StringComparison.Ordinal))
    throw new InvalidOperationException("示範產出不符合場景，需要重新核對程式行為。");

var data = new
{
    definition = definition.Text,
    definitionCaret = definition.CaretOffset,
    selection,
    surround = surround.Text,
    surroundOffset = surround.SurroundOffset,
    surroundLength = surround.SurroundLength,
    snippets = candidates,
    insert,
    insertCaret,
    execute,
    executeCaret,
    merge,
    mergeCaret,
    alterProcedure,
    alterFunction,
    gridValues,
    predicate
};
var destination = Path.Combine(Directory.GetCurrentDirectory(), "docs", "demos", "feature-demo-data.js");
var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(destination, "// 由 tools/Generate-FeatureDemoData.cs 產生，請勿手改 SQL。\nwindow.featureDemoData = "
    + json.Replace("\r\n", "\n", StringComparison.Ordinal) + ";\n", new UTF8Encoding(false));
Console.WriteLine($"已產生產品 SQL：F12、{candidates.Length} 個包夾片段、INSERT、EXEC、MERGE、ALTER PROCEDURE／FUNCTION、IN 條件。");
