using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// <c>SELECT … INTO #tmp</c> 建立的暫存資料表名冊。
/// </summary>
/// <remarks>
/// 這種寫法沒有資料行定義，帶型別的那份名冊（<c>SqlScriptTableCollector</c>）
/// 一個都不會收——但資料行仍然寫在使用者眼前，就在那句 <c>SELECT</c> 的選取清單裡。
/// 少了這一份的症狀是滑鼠停在自己上一行才建立的 <c>#Loan</c> 上什麼都沒有，
/// <c>#Loan.</c> 之後也一個欄位都列不出來。
/// </remarks>
public sealed class SqlSelectIntoTableTests
{
    private static SqlColumnSourceResolver Resolve(string sql) =>
        new(SqlTokenizer.Tokenize(sql));

    /// <summary>選取清單寫得出名稱時，資料行就是那幾個。</summary>
    [Fact]
    public void 讀出選取清單投影的資料行()
    {
        var resolver = Resolve("SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan");
        var table = resolver.FindSelectIntoTable("#Loan");

        Assert.NotNull(table);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, resolver.ResolveSelectIntoColumns(table!));
    }

    /// <summary>別名與運算式照外層看到的名稱走，與 CTE 同一份規則。</summary>
    [Fact]
    public void 別名就是外層看到的名稱()
    {
        var resolver = Resolve(
            "SELECT l.CopyNo AS Copy, COUNT(*) AS Total INTO #Loan FROM dbo.Loan l GROUP BY l.CopyNo");

        Assert.Equal(
            new[] { "Copy", "Total" },
            resolver.ResolveSelectIntoColumns(resolver.FindSelectIntoTable("#Loan")!));
    }

    /// <summary>
    /// 選取清單要問中繼資料時交出空清單。
    /// </summary>
    /// <remarks>
    /// <c>SELECT *</c> 打在資料庫的資料表上時，那份名單只有中繼資料知道，
    /// 而問這個問題的滑鼠停留路徑不等查詢。半份清單看起來與完整的一模一樣。
    /// </remarks>
    [Fact]
    public void 要問中繼資料時交出空清單()
    {
        var resolver = Resolve("SELECT * INTO #Loan FROM dbo.Loan");

        Assert.Empty(resolver.ResolveSelectIntoColumns(resolver.FindSelectIntoTable("#Loan")!));
    }

    /// <summary>參照自己的寫法不會一直展開下去。</summary>
    [Fact]
    public void 自我參照不會無限展開()
    {
        var resolver = Resolve("SELECT * INTO #Loan FROM #Loan");

        Assert.Empty(resolver.ResolveSelectIntoColumns(resolver.FindSelectIntoTable("#Loan")!));
    }

    /// <summary>
    /// <c>INSERT INTO</c> 不是一份宣告。
    /// </summary>
    /// <remarks>
    /// 兩者的形狀一模一樣，分辨的憑據只有這句敘述的第一個字。從 <c>INTO</c> 往回認
    /// 的話，使用者剛寫的那句 INSERT 會被讀成宣告，而它的「資料行」其實是插入目標
    /// 的資料行清單。
    /// </remarks>
    [Fact]
    public void 不把INSERT讀成宣告()
    {
        var resolver = Resolve("INSERT INTO #Loan (CopyNo) SELECT CopyNo FROM dbo.Loan");

        Assert.Null(resolver.FindSelectIntoTable("#Loan"));
    }

    /// <summary>
    /// 一般資料表不收。
    /// </summary>
    /// <remarks>
    /// <c>SELECT … INTO dbo.NewTable</c> 建的是一張真的資料表，那是中繼資料的事；
    /// 拿這份還沒執行的投影去蓋掉它，等於用「正要變成什麼樣」回答「現在長什麼樣」。
    /// </remarks>
    [Fact]
    public void 只收井號開頭的名稱()
    {
        var resolver = Resolve("SELECT CopyNo INTO dbo.NewLoan FROM dbo.Loan");

        Assert.Null(resolver.FindSelectIntoTable("NewLoan"));
        Assert.Null(resolver.FindSelectIntoTable("dbo.NewLoan"));
    }

    /// <summary>巢狀查詢裡的 <c>INTO</c> 屬於它自己。</summary>
    [Fact]
    public void 不認巢狀查詢裡的INTO()
    {
        var resolver = Resolve("SELECT CopyNo FROM dbo.Loan WHERE CopyNo IN (SELECT CopyNo FROM #Copy)");

        Assert.Null(resolver.FindSelectIntoTable("#Copy"));
    }

    /// <summary>結構預覽的指令碼分頁交出的是這一段原文，所以範圍要對得起來。</summary>
    [Fact]
    public void 記下整句在原文裡的範圍()
    {
        const string sql = "DECLARE @x INT; SELECT CopyNo INTO #Loan FROM dbo.Loan; SELECT 1";
        var table = Resolve(sql).FindSelectIntoTable("#Loan")!;

        Assert.Equal(
            "SELECT CopyNo INTO #Loan FROM dbo.Loan",
            sql.Substring(table.Start, table.End - table.Start));
    }

    /// <summary>
    /// 對外與 <c>CREATE TABLE #tmp (…)</c> 是同一個型別。
    /// </summary>
    /// <remarks>
    /// 下游（圖示、提交後的整句展開、滑鼠停留、結構預覽）問的都是同一件事：
    /// 這個名稱有哪些資料行。分成兩種型別的症狀是同一個名稱在四個表面各長一個樣。
    /// </remarks>
    [Fact]
    public void 兩種寫法交出同一個型別()
    {
        var resolver = Resolve(
            "CREATE TABLE #Copy (CopyNo NVARCHAR(20));" +
            "SELECT CopyNo, ReaderId INTO #Loan FROM dbo.Loan");

        Assert.Equal(new[] { "CopyNo" }, resolver.FindScriptTable("#Copy")!.ColumnNames);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, resolver.FindScriptTable("#Loan")!.ColumnNames);

        // 型別讀不出來，而空字串正是下游對「讀不出型別」的說法。
        Assert.Equal(string.Empty, resolver.FindScriptTable("#Loan")!.Columns[0].DataType);
        Assert.Null(resolver.FindScriptTable("Lib_Reader"));
    }

    /// <summary>
    /// 資料行延後算，而且同一個名稱只算一次。
    /// </summary>
    /// <remarks>
    /// <c>FROM </c> 之後的清單只要名稱，投影卻要把選取清單整段遞迴攤平。
    /// 每次重包一個新物件的話，那份延後就等於沒有——同一輪操作會攤平好幾次。
    /// </remarks>
    [Fact]
    public void 同一個名稱交出同一個物件()
    {
        var resolver = Resolve("SELECT CopyNo INTO #Loan FROM dbo.Loan");

        Assert.Same(resolver.FindScriptTable("#Loan"), resolver.FindScriptTable("#Loan"));
    }

    /// <summary>投影不出來時資料行是空的，呼叫端據此退回原本的行為。</summary>
    [Fact]
    public void 投影不出來時資料行是空的()
    {
        Assert.Empty(Resolve("SELECT * INTO #Loan FROM dbo.Loan").FindScriptTable("#Loan")!.Columns);
    }

    /// <summary>同名時保留先出現的那一句，與另外兩份名冊同一條規則。</summary>
    [Fact]
    public void 同名保留先出現的那一句()
    {
        var resolver = Resolve(
            "SELECT CopyNo INTO #Loan FROM dbo.Loan;" +
            "DROP TABLE #Loan;" +
            "SELECT ReaderId INTO #Loan FROM dbo.Loan");

        Assert.Equal(
            new[] { "CopyNo" },
            resolver.ResolveSelectIntoColumns(resolver.FindSelectIntoTable("#Loan")!));
    }
}
