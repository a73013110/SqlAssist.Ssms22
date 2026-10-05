using System.Linq;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

public sealed class SqlStatementHeadsTests
{
    [Theory]
    [InlineData("SELECT 1; SELECT 2", "SELECT|SELECT")]
    [InlineData("SELECT a FROM Loan\nUPDATE Loan SET a = 1", "SELECT|UPDATE")]
    [InlineData("SELECT 1\nGO\nTHROW 50000, 'x', 1", "SELECT|THROW")]
    [InlineData("BACKUP DATABASE LibArchive TO DISK = 'x'\nRESTORE VERIFYONLY FROM DISK = 'x'", "BACKUP|RESTORE")]
    [InlineData("SEND ON CONVERSATION @handle MESSAGE TYPE LoanRequest (@body)\nSEND ON CONVERSATION @handle", "SEND|SEND")]
    [InlineData("RECEIVE TOP (1) * FROM LoanQueue\nSELECT 1", "RECEIVE|SELECT")]
    [InlineData("MOVE CONVERSATION @handle TO @group\nGET CONVERSATION GROUP @group FROM LoanQueue", "MOVE|GET")]
    public void 每一句的第一個詞元與位置分析的界線相同(string sql, string expected)
    {
        var tokens = SqlTokenizer.Tokenize(sql);

        var heads = SqlStatementHeads.Find(sql, tokens).Select(index => tokens[index].Text);

        Assert.Equal(expected.Split('|'), heads);
    }
}
