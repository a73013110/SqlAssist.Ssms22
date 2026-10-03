using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>運算式的邊界：片語的 <c>{value}</c> 與 OFFSET 的值都問這一份。</summary>
public sealed class SqlOperandTests
{
    [Theory]
    [InlineData("FETCH NEXT 10", "10")]
    [InlineData("FETCH NEXT @a - @b + 1", "@a - @b + 1")]
    [InlineData("OFFSET (@page - 1) * @size", "(@page - 1) * @size")]
    [InlineData("SELECT JSON_OBJECT('k': l.Title", "l.Title")]
    [InlineData("SELECT JSON_ARRAY(UPPER(Title) + 'x'", "UPPER(Title) + 'x'")]
    [InlineData("SELECT JSON_ARRAY(COALESCE(Title, '')", "COALESCE(Title, '')")]
    [InlineData("SIZE = -1", "-1")]
    [InlineData("SIZE = 5", "5")]
    [InlineData("WHERE a = NULL", "NULL")]
    public void 往回走過一個運算式(string text, string expression)
    {
        var tokens = SqlTokenizer.Tokenize(text);
        var start = SqlOperand.SkipBackward(tokens, tokens.Count - 1);

        Assert.True(start >= 0);
        Assert.Equal(expression, text.Substring(tokens[start].Start));
    }

    /// <summary>寫不完運算元的尾巴不是值：關鍵字、運算子、左括號，以及認不出開頭的 <c>CASE … END</c>。</summary>
    [Theory]
    [InlineData("SELECT a +")]
    [InlineData("SELECT a FROM")]
    [InlineData("SELECT COUNT(")]
    [InlineData("SELECT CASE WHEN a = 1 THEN 1 END")]
    public void 不是運算元的結尾(string text)
    {
        var tokens = SqlTokenizer.Tokenize(text);

        Assert.Equal(-1, SqlOperand.SkipBackward(tokens, tokens.Count - 1));
    }
}
