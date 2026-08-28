using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 關鍵字目錄與位置過濾。
/// </summary>
/// <remarks>
/// 目錄本身是產生出來的，因此這裡驗的不是「有沒有列到某個字」，
/// 而是產生器與分析器對得起來：產生器說 DESC 只能出現在 ORDER BY 的欄位之後，
/// 分析器就必須在那個位置回報 OrderByTail，否則 DESC 永遠不會出現。
/// </remarks>
public sealed class SqlKeywordPositionTests
{
    [Fact]
    public void 目錄涵蓋以前手寫清單裡的關鍵字()
    {
        // 換掉手寫清單不能是退步：原本那 51 個字一個都不能少。
        string[] previouslyHandWritten =
        {
            "ALTER", "AND", "AS", "BEGIN", "BY", "CASE", "CREATE", "CROSS",
            "DECLARE", "DELETE", "DISTINCT", "DROP", "ELSE", "END", "EXEC",
            "EXECUTE", "EXISTS", "FROM", "FULL", "FUNCTION", "GROUP", "HAVING",
            "IF", "IN", "INNER", "INSERT", "INTO", "JOIN", "LEFT", "MERGE",
            "NOT", "NULL", "ON", "OR", "ORDER", "OUTER", "PROCEDURE", "RETURN",
            "RIGHT", "SELECT", "SET", "TABLE", "THEN", "TOP", "UNION", "UPDATE",
            "VALUES", "VIEW", "WHEN", "WHERE", "WITH"
        };

        var missing = previouslyHandWritten
            .Where(keyword => !SqlKeywordCatalog.IsKeyword(keyword))
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("USE")]
    [InlineData("GO")]
    [InlineData("RESTORE")]
    [InlineData("BACKUP")]
    [InlineData("TRUNCATE")]
    [InlineData("THROW")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("TRY_CONVERT")]
    [InlineData("IDENTITY_INSERT")]
    public void 目錄補上了手寫清單漏掉的關鍵字(string keyword)
    {
        // 後三個是 camelCase 補底線那一輪撈回來的，最容易在改產生器時掉。
        Assert.True(SqlKeywordCatalog.IsKeyword(keyword));
    }

    [Theory]
    [InlineData("", SqlKeywordPosition.StatementStart)]
    [InlineData("GO ", SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT 1; ", SqlKeywordPosition.StatementStart)]
    [InlineData("SELECT ", SqlKeywordPosition.SelectList)]
    [InlineData("SELECT a ", SqlKeywordPosition.SelectListTail)]
    [InlineData("SELECT * FROM ", SqlKeywordPosition.DataSource)]
    [InlineData("SELECT * FROM t ", SqlKeywordPosition.TableSourceTail)]
    [InlineData("SELECT * FROM t WHERE ", SqlKeywordPosition.Predicate)]
    [InlineData("SELECT * FROM t WHERE a = 1 ", SqlKeywordPosition.ExpressionTail)]
    [InlineData("SELECT * FROM t ORDER ", SqlKeywordPosition.ByAnchor)]
    [InlineData("SELECT * FROM t ORDER BY a ", SqlKeywordPosition.OrderByTail)]
    [InlineData("CREATE ", SqlKeywordPosition.DdlObject)]
    [InlineData("BEGIN ", SqlKeywordPosition.BlockStart)]
    [InlineData("SET ", SqlKeywordPosition.SetTarget)]
    [InlineData("INSERT ", SqlKeywordPosition.InsertTarget)]
    public void 分析器認得樣板對應的位置(string textBeforeToken, SqlKeywordPosition expected)
    {
        Assert.Equal(expected, SqlKeywordPositionAnalyzer.Analyze(textBeforeToken));
    }

    [Fact]
    public void 加引號的識別字不當成關鍵字()
    {
        // FROM [FROM] 裡的 [FROM] 是資料表名稱，游標在它後面是資料來源之後、
        // 不是 FROM 之後。
        Assert.Equal(
            SqlKeywordPosition.TableSourceTail,
            SqlKeywordPositionAnalyzer.Analyze("SELECT * FROM [FROM] "));
    }

    [Theory]
    [InlineData("SELECT * FROM t ORDER BY a ", "DESC", true)]
    [InlineData("", "DESC", false)]
    [InlineData("SELECT * FROM t WHERE ", "DESC", false)]
    [InlineData("", "SELECT", true)]
    [InlineData("", "USE", true)]
    [InlineData("", "RESTORE", true)]
    [InlineData("SELECT * FROM t WHERE ", "PROCEDURE", false)]
    [InlineData("CREATE ", "PROCEDURE", true)]
    public void 位置過濾決定關鍵字出不出現(string textBeforeCaret, string keyword, bool expected)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret + keyword.Substring(0, 1));
        var suggestions = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty);

        var matched = SuggestionMatcher
            .Filter(suggestions, context)
            .Any(suggestion =>
                suggestion.Kind == SuggestionKind.Keyword &&
                suggestion.DisplayText == keyword);

        Assert.Equal(expected, matched);
    }

    [Fact]
    public void 產生器判不出位置的關鍵字一律放行()
    {
        // FILLFACTOR 這種深層子句字沒有樣板涵蓋得到。分不出位置的代價是多幾個字，
        // 猜錯位置的代價是使用者永遠打不出來——所以 fail-open。
        Assert.Equal(SqlKeywordPosition.Any, SqlKeywordCatalog.GetPositions("FILLFACTOR"));
    }
}
