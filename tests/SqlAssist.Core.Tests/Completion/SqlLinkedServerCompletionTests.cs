using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 限定字停在哪一格，決定那一格能列出哪些東西。
/// </summary>
/// <remarks>
/// 四段式名稱是一路往右打出來的，每打一個點號就換一個名稱空間：
/// 伺服器之後只有資料庫、資料庫之後是結構描述與物件、結構描述之後只有物件。
/// 混一格進來的症狀是清單看起來很豐富，而選中的名稱在那個位置根本不合法。
/// </remarks>
public sealed class SqlLinkedServerCompletionTests
{
    private static readonly SqlSuggestion Server =
        new("LibMirror", "LibMirror", "Linked server", "連結伺服器 LibMirror", SuggestionKind.LinkedServer);

    private static readonly SqlSuggestion Database =
        new("LibArchive", "LibArchive", "Database", "USE LibArchive", SuggestionKind.Database);

    private static readonly SqlSuggestion Schema =
        new("dbo", "dbo.", "Schema", "Schema dbo", SuggestionKind.Schema, schemaName: "dbo");

    private static readonly SqlSuggestion Table =
        new("Loan", "Loan", "Table", "Loan", SuggestionKind.Table, schemaName: "dbo");

    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    /// <summary>照中繼資料認出的結果重新對齊，再過濾一次。</summary>
    private static string[] Filter(string sqlWithCaret, SqlQualifierSlot? leftmost = null)
    {
        var context = Analyze(sqlWithCaret);

        if (leftmost is { } slot && context.QualifierPath!.TryRealign(slot, out var realigned))
        {
            context = context.WithQualifierPath(realigned);
        }

        return SuggestionMatcher
            .Filter(new[] { Server, Database, Schema, Table }, context)
            .Select(suggestion => suggestion.DisplayText)
            .ToArray();
    }

    /// <remarks>
    /// 三段式與四段式名稱都從這一格開始打，所以資料庫與連結伺服器都要出得來。
    /// 少了它們，使用者只能整串自己打——而他要跨的那幾個庫正是記不起全名的那些。
    /// </remarks>
    [Fact]
    public void 資料來源位置列得出資料庫與連結伺服器()
    {
        var names = Filter("SELECT * FROM |");

        Assert.Contains("LibArchive", names);
        Assert.Contains("LibMirror", names);
        Assert.Contains("Loan", names);
    }

    /// <remarks>
    /// <c>SELECT LibArchive.dbo.fn_Fee(1)</c> 是合法的，所以運算式位置也要有
    /// 第一段。只補資料來源那一格的話，使用者會遇到「同一個名稱在 FROM 之後
    /// 建議得出來、在 SELECT 之後就沒有」，而語法明明都合法。
    /// </remarks>
    [Fact]
    public void 運算式位置也列得出第一段()
    {
        var names = Filter("SELECT | FROM Loan");

        Assert.Contains("LibArchive", names);
        Assert.Contains("LibMirror", names);
    }

    /// <remarks>
    /// <c>EXEC LibArchive.dbo.usp_Renew</c> 同理；<c>NEXT VALUE FOR</c> 與
    /// <c>APPLY</c> 走的是同一條規則。
    /// </remarks>
    [Fact]
    public void EXEC之後也列得出第一段()
    {
        Assert.Contains("LibArchive", Filter("EXEC |"));
    }

    /// <remarks>
    /// 接不到物件的位置就不收：資料表提示那一格放進來的話，每一次按鍵都要多背
    /// 一份一定比不中的名單。
    /// </remarks>
    [Fact]
    public void 接不到物件的位置不列第一段()
    {
        var names = Filter("SELECT * FROM Loan WITH (|");

        Assert.DoesNotContain("LibArchive", names);
        Assert.DoesNotContain("LibMirror", names);
    }

    [Fact]
    public void USE之後仍然列資料庫()
    {
        Assert.Equal(new[] { "LibArchive" }, Filter("USE |"));
    }

    /// <remarks>
    /// 連結伺服器之後只有資料庫接得上。物件與結構描述要再往右一格才出得來，
    /// 這裡放行的話清單會列出一批選了就寫成三段式、而那台伺服器上查不到的名稱。
    /// </remarks>
    [Fact]
    public void 連結伺服器之後只列資料庫()
    {
        Assert.Equal(new[] { "LibArchive" }, Filter("SELECT * FROM LibMirror.|", SqlQualifierSlot.Server));
    }

    /// <remarks>
    /// 這一格是使用者最常打的跨資料庫寫法。認不出 <c>LibArchive</c> 是資料庫的話，
    /// 右對齊會把它當成結構描述，於是整份清單一筆都比不中——而畫面上只是沒有建議。
    ///
    /// 結構描述不在裡面，因為 <c>FROM</c> 這一類位置本來就只收資料來源
    /// （<c>IsAllowedForTarget</c>）。跨資料庫沿用同一條而不開特例：物件清單已經
    /// 涵蓋每一個結構描述，而提交時 <c>SqlInsertionText</c> 會補上正確的那一個。
    /// </remarks>
    [Fact]
    public void 資料庫之後列那個資料庫的物件()
    {
        Assert.Equal(new[] { "Loan" }, Filter("SELECT * FROM LibArchive.|", SqlQualifierSlot.Database));
    }

    [Fact]
    public void 伺服器加資料庫之後列那個資料庫的物件()
    {
        Assert.Equal(
            new[] { "Loan" },
            Filter("SELECT * FROM LibMirror.LibArchive.|", SqlQualifierSlot.Server));
    }

    /// <remarks>
    /// 往右走過任何一格之後就不可能再是一台伺服器：T-SQL 沒有五段式名稱。
    /// </remarks>
    [Fact]
    public void 結構描述之後不列連結伺服器()
    {
        Assert.Equal(new[] { "Loan" }, Filter("SELECT * FROM dbo.|"));
    }

    /// <remarks>
    /// 名稱的中間段只寫名稱本身，點號由使用者自己打——而打出點號要能把清單
    /// 重開起來，否則使用者得再多打一個字元才看得到下一段。
    ///
    /// 以位址命名的連結伺服器是這裡唯一的陷阱：剝掉方括號之後它以數字開頭，
    /// 與 <c>1.5</c> 這種小數長得一樣，而擋小數正是這條規則本來的用意。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM LibMirror.")]
    [InlineData("SELECT * FROM [LibMirror].")]
    [InlineData("SELECT * FROM [192.0.2.10].")]
    [InlineData("SELECT * FROM [192.0.2.10].[LibArchive].")]
    public void 自己打出點號之後清單要重開(string textBeforeCaret)
    {
        Assert.True(SqlCompletionTriggers.ShouldReopen(textBeforeCaret));
    }

    /// <remarks>
    /// 小數仍然要擋：分不出來的代價是每次輸入小數點都彈出整個資料庫的物件清單。
    /// </remarks>
    [Fact]
    public void 小數點不重開清單()
    {
        Assert.False(SqlCompletionTriggers.ShouldReopen("SELECT * FROM Loan WHERE Fee > 1."));
    }
}
