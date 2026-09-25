using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 建議清單列尾的例外標記：大部分列一顆都沒有，有標記的那一列才讀得出不同。
/// </summary>
[Collection(nameof(SqlSuggestionUsageCollection))]
public sealed class SuggestionMarkTests
{
    private static SqlSuggestion Procedure(string name, string schema)
    {
        return new SqlSuggestion(name, $"{schema}.{name}", string.Empty, string.Empty, SuggestionKind.Procedure, schemaName: schema);
    }

    private static SuggestionMark MarksAt(string textBeforeCaret, SqlSuggestion suggestion)
    {
        return SuggestionMarks.Of(suggestion, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret));
    }

    [Fact]
    public void 一般列沒有任何標記()
    {
        SqlSuggestionUsage.Clear();
        var context = SqlCompletionContextAnalyzer.Analyze("SELECT ");
        var builtIn = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);

        Assert.All(
            builtIn.Where(item => !item.IsDestructive),
            item => Assert.Equal(SuggestionMark.None, SuggestionMarks.Of(item, context)));
    }

    /// <summary>標記與排名讀同一份使用紀錄，兩者不會說出不同的答案。</summary>
    [Fact]
    public void 最近提交過的才標最近用過()
    {
        SqlSuggestionUsage.Clear();

        try
        {
            var used = Procedure("usp_LoanReport", "dbo");
            var other = Procedure("usp_ReaderReport", "dbo");
            SqlSuggestionUsage.Record(used);

            Assert.Equal(SuggestionMark.RecentlyUsed, MarksAt("EXEC ", used));
            Assert.Equal(SuggestionMark.None, MarksAt("EXEC ", other));
        }
        finally
        {
            SqlSuggestionUsage.Clear();
        }
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("df")]
    [InlineData("mg")]
    [InlineData("dt")]
    public void 危險片段標危險(string shortcut)
    {
        SqlSuggestionUsage.Clear();
        var snippet = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Single(item => item.Kind == SuggestionKind.Snippet && item.DisplayText == shortcut);

        Assert.Equal(SuggestionMark.Destructive, MarksAt("", snippet));
    }

    [Theory]
    [InlineData("TEXT", true)]
    [InlineData("NTEXT", true)]
    [InlineData("IMAGE", true)]
    [InlineData("NVARCHAR", false)]
    [InlineData("VARBINARY", false)]
    [InlineData("XML", false)]
    public void 只有淘汰的大型物件型別標已淘汰(string typeName, bool deprecated)
    {
        SqlSuggestionUsage.Clear();
        var type = SqlDataTypeCatalog.All.Single(item => item.DisplayText == typeName);

        Assert.Equal(
            deprecated ? SuggestionMark.Deprecated : SuggestionMark.None,
            MarksAt("DECLARE @x ", type));
    }

    /// <summary>
    /// 資料行型別是 <c>ntext</c> 時不標：那一列選的是資料行，改不了它的型別。
    /// </summary>
    [Fact]
    public void 資料行不因型別標已淘汰()
    {
        SqlSuggestionUsage.Clear();
        var column = new SqlSuggestion("Note", "Note", "ntext NULL", string.Empty, SuggestionKind.Column, schemaName: "dbo");

        Assert.Equal(SuggestionMark.None, MarksAt("SELECT ", column));
    }

    /// <summary><c>EXEC |</c> 系統程序與使用者程序並列，只有系統的那一筆要標。</summary>
    [Fact]
    public void EXEC之後的系統程序標系統物件()
    {
        SqlSuggestionUsage.Clear();

        Assert.Equal(SuggestionMark.SystemObject, MarksAt("EXEC ", Procedure("sp_help", "sys")));
        Assert.Equal(SuggestionMark.None, MarksAt("EXEC ", Procedure("usp_LoanReport", "dbo")));
    }

    /// <summary>限定字就是系統結構描述時整份都是系統物件，標了等於每一列都標。</summary>
    [Theory]
    [InlineData("EXEC sys.")]
    [InlineData("SELECT * FROM sys.")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.")]
    public void 系統結構描述之後不標系統物件(string textBeforeCaret)
    {
        SqlSuggestionUsage.Clear();

        Assert.Equal(SuggestionMark.None, MarksAt(textBeforeCaret, Procedure("sp_help", "sys")));
    }

    /// <summary>
    /// 結構描述名稱本身與系統檢視的資料行都帶著 <c>sys</c>，但都不是系統物件。
    /// </summary>
    [Fact]
    public void 系統結構描述名稱與系統檢視的資料行不標()
    {
        SqlSuggestionUsage.Clear();
        var schema = BuiltInSuggestionCatalog.Create(SqlSnippetLibrary.Empty)
            .Single(item => item.Kind == SuggestionKind.Schema && item.DisplayText == "sys");
        var column = new SqlSuggestion("name", "name", "sysname NOT NULL", string.Empty, SuggestionKind.Column, schemaName: "sys");

        Assert.Equal(SuggestionMark.None, MarksAt("EXEC ", schema));
        Assert.Equal(SuggestionMark.None, MarksAt("SELECT * FROM sys.objects o WHERE o.", column));
    }

    [Fact]
    public void 標記可以並存()
    {
        SqlSuggestionUsage.Clear();

        try
        {
            var system = Procedure("sp_help", "sys");
            SqlSuggestionUsage.Record(system);

            Assert.Equal(SuggestionMark.SystemObject | SuggestionMark.RecentlyUsed, MarksAt("EXEC ", system));
        }
        finally
        {
            SqlSuggestionUsage.Clear();
        }
    }
}
