using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// <c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 底下的系統物件何時該拉進來。
/// </summary>
/// <remarks>
/// 那一份光是一個使用者資料庫底下就有一兩千筆。判斷錯的代價不是少幾筆建議，
/// 是打第一個字元時整份清單被 <c>sp_</c> 開頭的名稱淹掉。
/// </remarks>
public sealed class SqlSystemObjectTests
{
    [Theory]
    [InlineData("SELECT * FROM sys.")]
    [InlineData("SELECT * FROM sys.dm_exec")]
    [InlineData("SELECT * FROM SYS.")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.")]
    [InlineData("EXEC ")]
    [InlineData("EXECUTE sp_help")]
    [InlineData("EXEC sys.")]
    public void 這些位置要系統物件(string textBeforeCaret)
    {
        Assert.True(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).WantsSystemObjects);
    }

    /// <summary>
    /// <c>ALTER PROCEDURE </c> 目標同樣是預存程序，但系統程序改不動。
    /// </summary>
    /// <remarks>
    /// 列出來只會讓使用者選到一個改不了的東西——與內建函式不進
    /// <c>ALTER FUNCTION</c> 是同一條理由。
    /// </remarks>
    [Theory]
    [InlineData("ALTER PROCEDURE ")]
    [InlineData("SELECT * FROM dbo.")]
    [InlineData("SELECT * FROM ")]
    [InlineData("SELECT ")]
    [InlineData("SELECT * FROM dbo.Loan a WHERE a.")]
    public void 這些位置不要系統物件(string textBeforeCaret)
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).WantsSystemObjects);
    }

    /// <summary>
    /// 兩個系統結構描述不必等中繼資料。
    /// </summary>
    /// <remarks>
    /// 第一層查詢刻意不收它們（那會連帶把一兩千個系統物件拉進來），
    /// 少了這兩筆的話，使用者連「打 sys 再按 Tab」這條路都沒有。
    /// </remarks>
    [Theory]
    [InlineData("sys")]
    [InlineData("INFORMATION_SCHEMA")]
    public void 系統結構描述不必等連線(string schema)
    {
        var builtIn = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);
        var item = builtIn.Single(entry =>
            entry.Kind == SuggestionKind.Schema && entry.DisplayText == schema);

        Assert.Equal(schema + ".", item.InsertionText);
        Assert.True(item.TriggerFollowUp);
    }

    /// <summary>打 <c>sys</c> 就找得到，而且提交之後接著列出它底下的東西。</summary>
    [Fact]
    public void 打出前綴就找得到系統結構描述()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT * FROM t WHERE x = sys");
        var ranked = SuggestionMatcher.Match(
            BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty),
            context);

        Assert.Contains(ranked, item => item.DisplayText == "sys");
    }
}
