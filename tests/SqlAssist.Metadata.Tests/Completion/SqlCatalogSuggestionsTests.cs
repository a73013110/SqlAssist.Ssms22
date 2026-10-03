using SqlAssist.Core.Completion;
using SqlAssist.Metadata.Completion;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Completion;

public sealed class SqlCatalogSuggestionsTests
{
    /// <summary>
    /// 同義字有自己的種類：<c>DROP SYNONYM</c> 只列它們，<c>FROM</c> 之後照樣與資料表同格。
    /// </summary>
    [Fact]
    public void 同義字是自己的種類()
    {
        var suggestion = Assert.Single(SqlCatalogSuggestions.Objects(new[]
        {
            new SqlObjectInfo(1, "dbo", "syn_Loan", SqlObjectKind.Synonym)
        }));

        Assert.Equal(SuggestionKind.Synonym, suggestion.Kind);
        Assert.Equal("dbo", suggestion.SchemaName);
    }
}
