using System;
using System.Linq;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

public sealed class SqlStatementBoundariesTests
{
    /// <summary>
    /// 換行是隱含的界線，但剖析器不看換行：前一句寫到這裡接得上的字仍屬於前一句。
    /// </summary>
    /// <remarks>
    /// <c>OFFSET 0 ROWS</c> 已經是完整的一句，FETCH 又能開始一句（資料指標），分得開它們的是
    /// 前一句的片語接不接得上。當成開頭的症狀是語句說明開出資料指標的 FETCH，
    /// 範圍分析把 <c>FETCH NEXT 5 ROWS ONLY</c> 切成下一句。
    /// </remarks>
    [Theory]
    [InlineData("SELECT a FROM t ORDER BY a\nOFFSET 0 ROWS\nFETCH NEXT 5 ROWS ONLY", "FETCH")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 0 ROWS\nFETCH NEXT 5 ROWS ONLY", "FETCH")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 1 ROW\nFETCH FIRST 1 ROW ONLY", "FETCH")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY", "FETCH")]
    [InlineData("ALTER TABLE Loan\nDROP COLUMN DueDate", "DROP")]
    [InlineData("ALTER DATABASE Lib_Db\nSET RECOVERY SIMPLE", "SET")]
    public void 前一句接得上的字不是句首(string text, string word)
    {
        Assert.False(IsHead(text, word));
    }

    /// <summary>接不上前一句的時候，同一個字照樣開始一句：資料指標的 FETCH 不因 OFFSET-FETCH 而消失。</summary>
    [Theory]
    [InlineData("OPEN c\nFETCH NEXT FROM c INTO @CopyNo", "FETCH")]
    [InlineData("SELECT a FROM t ORDER BY a\nFETCH NEXT FROM c INTO @CopyNo", "FETCH")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY\nFETCH NEXT FROM c INTO @CopyNo", "FETCH", 1)]
    [InlineData("WHILE @@FETCH_STATUS = 0\nBEGIN\n    FETCH NEXT FROM c INTO @CopyNo\nEND", "FETCH")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 0 ROWS\nSELECT 1", "SELECT", 1)]
    [InlineData("SELECT a FROM Loan\nDROP TABLE #Loan", "DROP")]
    public void 接不上前一句的字仍是句首(string text, string word, int occurrence = 0)
    {
        Assert.True(IsHead(text, word, occurrence));
    }

    private static bool IsHead(string text, string word, int occurrence = 0)
    {
        var tokens = SqlTokenizer.Tokenize(text);
        var index = Enumerable.Range(0, tokens.Count)
            .Where(i => string.Equals(tokens[i].Value, word, StringComparison.OrdinalIgnoreCase))
            .ElementAt(occurrence);

        return new SqlStatementBoundaries(text, tokens).IsStatementHead(index);
    }
}
