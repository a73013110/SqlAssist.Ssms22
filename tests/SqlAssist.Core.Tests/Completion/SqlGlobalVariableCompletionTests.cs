using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// <c>@@</c> 之後的全域變數建議。
/// </summary>
public sealed class SqlGlobalVariableCompletionTests
{
    [Theory]
    [InlineData("SELECT @@")]
    [InlineData("SELECT @@ROW")]
    [InlineData("IF @@ERR")]
    [InlineData("PRINT @@VER")]
    [InlineData("SELECT * FROM t WHERE a = @@SP")]
    [InlineData("SET @rows = @@ROWC")]
    [InlineData("SELECT a, @@")]
    public void 兩個小老鼠之後建議全域變數(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.GlobalVariable, context.Target);
    }

    /// <summary>
    /// 適用範圍必須從第一個小老鼠開始。
    /// </summary>
    /// <remarks>
    /// 詞元起點落在 <c>ROW</c> 上的話，提交 <c>@@ROWCOUNT</c> 只會取代 <c>ROW</c>，
    /// 編輯器裡留下的是 <c>@@@@ROWCOUNT</c>。
    /// </remarks>
    [Theory]
    [InlineData("SELECT @@ROW", "@@ROW")]
    [InlineData("SELECT @@", "@@")]
    [InlineData("IF @@ERROR", "@@ERROR")]
    public void 前綴含前面的小老鼠(string textBeforeCaret, string expectedPrefix)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(expectedPrefix, context.Prefix);
        Assert.Equal(textBeforeCaret.Length - expectedPrefix.Length, context.TokenStart);
    }

    /// <summary>
    /// 單一個小老鼠不是全域變數。
    /// </summary>
    /// <remarks>
    /// 不管那個位置是在宣告還是在引用，走的都是另一條路
    /// （<see cref="CompletionTarget.Variable"/>，見 <c>SqlScriptVariableTests</c>）。
    /// 兩者分開的理由就是名稱的來源不同：一份是系統的，一份是使用者自己寫的。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @")]
    [InlineData("DECLARE @pub")]
    [InlineData("SELECT @")]
    [InlineData("SELECT * FROM t WHERE a = @pa")]
    [InlineData("CREATE PROCEDURE p @loan")]
    public void 單一個小老鼠不是全域變數(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.NotEqual(CompletionTarget.GlobalVariable, context.Target);
    }

    /// <summary>字串與註解裡的小老鼠不算。</summary>
    [Theory]
    [InlineData("SELECT '@@")]
    [InlineData("-- @@")]
    [InlineData("/* @@")]
    public void 字串與註解裡不建議(string textBeforeCaret)
    {
        Assert.False(SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).IsValid);
    }

    [Fact]
    public void 這個位置只列全域變數()
    {
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT @@");
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty)
            .Concat(SqlGlobalVariableCatalog.All)
            .Concat(new[]
            {
                new SqlSuggestion("Lib_Reader", "Lib_Reader", "資料表", "資料表", SuggestionKind.Table),
                new SqlSuggestion("PUBL_CODE", "PUBL_CODE", "欄位", "欄位", SuggestionKind.Column)
            });

        var filtered = SuggestionMatcher.Filter(candidates, context);

        Assert.NotEmpty(filtered);
        Assert.All(filtered, item => Assert.Equal(SuggestionKind.GlobalVariable, item.Kind));
    }

    /// <summary>
    /// 反過來，一般位置的清單裡一個全域變數都不該有。
    /// </summary>
    /// <remarks>
    /// 混進去的話，每一次按鍵都要多比對 31 個一定比不中的名稱，
    /// 而使用者在那些位置從來不是要它們。
    /// </remarks>
    [Theory]
    [InlineData("SELECT ROW")]
    [InlineData("SELECT * FROM PUB")]
    [InlineData("WHERE ERR")]
    public void 一般位置不列全域變數(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var filtered = SuggestionMatcher.Filter(SqlGlobalVariableCatalog.All, context);

        Assert.Empty(filtered);
    }

    /// <summary>打完前綴之後排名要挑得出正確的那一個。</summary>
    [Theory]
    [InlineData("SELECT @@ROWC", "@@ROWCOUNT")]
    [InlineData("SELECT @@VERS", "@@VERSION")]
    [InlineData("IF @@FETCH", "@@FETCH_STATUS")]
    [InlineData("SELECT @@TRAN", "@@TRANCOUNT")]
    public void 前綴比對排在第一(string textBeforeCaret, string expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var ranked = SuggestionMatcher.Match(SqlGlobalVariableCatalog.All, context);

        Assert.NotEmpty(ranked);
        Assert.Equal(expected, ranked[0].DisplayText);
    }
}
