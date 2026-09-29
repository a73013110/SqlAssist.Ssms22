using System.Linq;
using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 游標名稱：<c>OPEN</c>、<c>CLOSE</c>、<c>DEALLOCATE</c>、<c>FETCH</c>、<c>WHERE CURRENT OF</c> 之後
/// 列出這份指令碼宣告的游標。
/// </summary>
public sealed class SqlCursorCompletionTests
{
    private const string Declared =
        "DECLARE @CopyNo INT;\r\nDECLARE c CURSOR FOR SELECT TOP 10 CopyNo FROM dbo.Cat_BookCopy\r\n";

    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    private static string[] ScriptSources(string sqlWithCaret) =>
        Analyze(sqlWithCaret).ScriptSources.Select(suggestion => suggestion.DisplayText).ToArray();

    [Theory]
    [InlineData("OPEN |")]
    [InlineData("OPEN GLOBAL |")]
    [InlineData("CLOSE |")]
    [InlineData("DEALLOCATE |")]
    [InlineData("OPEN c\r\nFETCH |")]
    [InlineData("OPEN c\r\nFETCH NEXT FROM |")]
    [InlineData("OPEN c\r\nFETCH ABSOLUTE 5 FROM |")]
    [InlineData("OPEN c\r\nFETCH NEXT FROM GLOBAL |")]
    [InlineData("UPDATE dbo.Cat_BookCopy SET CopyNo = 1 WHERE CURRENT OF |")]
    public void 游標動詞之後列出宣告的游標(string tail)
    {
        var context = Analyze(Declared + tail);

        Assert.Equal(CompletionTarget.Cursor, context.Target);
        Assert.Equal(SqlCompletionSlot.Grammar, context.Slot);
        Assert.Equal(new[] { "c" }, context.ScriptSources.Select(suggestion => suggestion.DisplayText));
    }

    [Fact]
    public void 打了字也還是游標那一格()
    {
        var context = Analyze(Declared + "OPEN c|");

        Assert.Equal(CompletionTarget.Cursor, context.Target);
        Assert.Equal("c", context.Prefix);
    }

    /// <summary>ISO 選項、方括號名稱都認；游標變數是變數，不在這一份。</summary>
    [Fact]
    public void 名冊只收具名游標()
    {
        Assert.Equal(
            new[] { "c1", "Loan cursor" },
            ScriptSources(
                "DECLARE c1 INSENSITIVE SCROLL CURSOR FOR SELECT 1;\r\n" +
                "DECLARE [Loan cursor] CURSOR LOCAL FAST_FORWARD FOR SELECT 1;\r\n" +
                "DECLARE @v CURSOR;\r\n" +
                "DECLARE c1 CURSOR FOR SELECT 2;\r\n" +
                "OPEN |"));
    }

    /// <summary>
    /// GLOBAL 只在游標動詞之後是修飾字：查詢的 <c>FROM GLOBAL</c> 是一張資料表。
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM GLOBAL |")]
    [InlineData("SELECT * FROM |")]
    public void 查詢的FROM不是游標(string tail)
    {
        Assert.NotEqual(CompletionTarget.Cursor, Analyze(Declared + tail).Target);
    }

    /// <summary>
    /// <c>OPEN</c>、<c>CLOSE</c> 之後也可以是金鑰：片語的字不看目標，與資料指標名稱並列。
    /// </summary>
    [Theory]
    [InlineData("OPEN c\r\nFETCH |", "NEXT")]
    [InlineData("OPEN |", "SYMMETRIC")]
    [InlineData("OPEN |", "MASTER")]
    [InlineData("CLOSE |", "ALL SYMMETRIC KEYS")]
    [InlineData("DEALLOCATE |", "GLOBAL")]
    public void 游標那一格只留游標與片語的字(string tail, string phraseWord)
    {
        var context = Analyze(Declared + tail);
        var candidates = new[]
        {
            new SqlSuggestion("c", "c", "", "", SuggestionKind.Cursor),
            new SqlSuggestion("Cat_BookCopy", "Cat_BookCopy", "", "", SuggestionKind.Table, schemaName: "dbo"),
            new SqlSuggestion("SELECT", "SELECT", "", "", SuggestionKind.Keyword)
        }.Concat(context.ClausePhrase!.Suggestions);

        var names = SuggestionContextFilter.Filter(candidates, context).Select(item => item.DisplayText).ToArray();

        Assert.Contains("c", names);
        Assert.Contains(phraseWord, names);
        Assert.DoesNotContain("Cat_BookCopy", names);
        Assert.DoesNotContain("SELECT", names);
    }

    /// <summary>金鑰名稱與 <c>DECRYPTION BY</c> 之後的名稱不是資料指標。</summary>
    [Theory]
    [InlineData("OPEN SYMMETRIC KEY |")]
    [InlineData("CLOSE SYMMETRIC KEY |")]
    [InlineData("OPEN SYMMETRIC KEY k DECRYPTION BY CERTIFICATE |")]
    [InlineData("CLOSE ALL SYMMETRIC KEYS |")]
    public void 金鑰那一格不列資料指標(string tail)
    {
        var context = Analyze(Declared + tail);
        var cursor = new SqlSuggestion("c", "c", "", "", SuggestionKind.Cursor);

        Assert.NotEqual(CompletionTarget.Cursor, context.Target);
        Assert.Empty(SuggestionContextFilter.Filter(new[] { cursor }, context));
    }

    [Fact]
    public void 一般位置不列游標()
    {
        var context = Analyze(Declared + "SELECT |");
        var cursor = new SqlSuggestion("c", "c", "", "", SuggestionKind.Cursor);

        Assert.Empty(SuggestionContextFilter.Filter(new[] { cursor }, context));
    }
}
