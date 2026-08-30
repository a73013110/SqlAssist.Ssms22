using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 觸發程序、序列與使用者自訂資料表型別各自的位置。
/// </summary>
/// <remarks>
/// 三者都不進一般清單：<c>SELECT tr|</c> 不該冒出觸發程序，
/// 而 <c>EXEC </c> 之後選到一個觸發程序一定執行失敗。
/// </remarks>
public sealed class SqlObjectClassTargetTests
{
    [Theory]
    [InlineData("ALTER TRIGGER ")]
    [InlineData("DROP TRIGGER ")]
    [InlineData("DISABLE TRIGGER ")]
    [InlineData("ENABLE TRIGGER ")]
    public void 觸發程序的位置(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.Trigger, context.Target);
    }

    /// <summary>
    /// 觸發程序與模組一樣改得動，因此 <c>ALTER</c> 之後同樣放進完整定義。
    /// </summary>
    [Fact]
    public void ALTER之後要展開定義()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("ALTER TRIGGER ");

        Assert.Equal(CompletionIntent.AlterDefinition, context.Intent);
        Assert.Equal(0, context.TargetKeywordStart);
    }

    /// <summary>其餘三個只是引用名稱，不該把整份定義塞進編輯器。</summary>
    [Theory]
    [InlineData("DROP TRIGGER ")]
    [InlineData("DISABLE TRIGGER ")]
    [InlineData("ENABLE TRIGGER ")]
    public void 其餘位置只插入名稱(string textBeforeCaret)
    {
        Assert.Equal(
            CompletionIntent.Reference,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Intent);
    }

    [Theory]
    [InlineData("SELECT NEXT VALUE FOR ")]
    [InlineData("INSERT INTO dbo.Loan (LoanId) VALUES (NEXT VALUE FOR ")]
    [InlineData("ALTER SEQUENCE ")]
    [InlineData("DROP SEQUENCE ")]
    public void 序列的位置(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.Sequence, context.Target);
    }

    /// <summary>
    /// 使用者自訂的資料表型別與內建型別在同一個位置，包括帶結構描述的寫法。
    /// </summary>
    [Theory]
    [InlineData("DECLARE @copies dbo.")]
    [InlineData("CREATE PROCEDURE p @copies dbo.")]
    [InlineData("SELECT CAST(x AS dbo.")]
    public void 帶結構描述的型別位置(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.DataType, context.Target);
        Assert.Equal("dbo", context.Qualifier);
    }

    /// <summary>
    /// 限定字要帶著走，否則結構描述過濾攔不住別的結構描述的型別。
    /// </summary>
    /// <remarks>
    /// 內建型別沒有結構描述，因此在 <c>dbo.</c> 之後會被同一條過濾擋掉——
    /// <c>dbo.INT</c> 不是東西。
    /// </remarks>
    [Fact]
    public void 帶結構描述時不列內建型別()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("DECLARE @copies dbo.");

        Assert.Empty(SuggestionMatcher.Filter(SqlDataTypeCatalog.All, context));
    }

    [Fact]
    public void 沒有結構描述時照列內建型別()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("DECLARE @copies ");

        Assert.NotEmpty(SuggestionMatcher.Filter(SqlDataTypeCatalog.All, context));
    }

    /// <summary>三種新類別都只在自己的位置出現。</summary>
    [Theory]
    [InlineData("SELECT tr")]
    [InlineData("SELECT * FROM se")]
    [InlineData("EXEC ")]
    [InlineData("ALTER PROCEDURE ")]
    public void 一般位置不列這三類(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var candidates = new[]
        {
            new SqlSuggestion("trg_Loan_Audit", "trg_Loan_Audit", "", "", SuggestionKind.Trigger, schemaName: "dbo"),
            new SqlSuggestion("seq_LoanId", "seq_LoanId", "", "", SuggestionKind.Sequence, schemaName: "dbo"),
            new SqlSuggestion("CopyList", "CopyList", "", "", SuggestionKind.UserDefinedType, schemaName: "dbo")
        };

        Assert.Empty(SuggestionMatcher.Filter(candidates, context));
    }

    [Fact]
    public void 各自的位置列得出來()
    {
        var trigger = new SqlSuggestion(
            "trg_Loan_Audit", "trg_Loan_Audit", "", "", SuggestionKind.Trigger, schemaName: "dbo");
        var sequence = new SqlSuggestion(
            "seq_LoanId", "seq_LoanId", "", "", SuggestionKind.Sequence, schemaName: "dbo");
        var tableType = new SqlSuggestion(
            "CopyList", "CopyList", "", "", SuggestionKind.UserDefinedType, schemaName: "dbo");
        var candidates = new[] { trigger, sequence, tableType };

        Assert.Equal(
            new[] { trigger },
            SuggestionMatcher.Filter(candidates, SqlCompletionContextAnalyzer.Analyze("ALTER TRIGGER ")).ToArray());
        Assert.Equal(
            new[] { sequence },
            SuggestionMatcher.Filter(candidates, SqlCompletionContextAnalyzer.Analyze("SELECT NEXT VALUE FOR ")).ToArray());
        Assert.Equal(
            new[] { tableType },
            SuggestionMatcher.Filter(candidates, SqlCompletionContextAnalyzer.Analyze("DECLARE @copies ")).ToArray());
    }
}
