using System.Linq;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Wildcards;
using Xunit;

namespace SqlAssist.Core.Tests.Wildcards;

public sealed class SqlWildcardAnalyzerTests
{
    private static SqlWildcardTarget? Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlWildcardAnalyzer.Analyze(input.Text, input.Caret);
    }

    private static SqlWildcardTarget Expand(string sqlWithCaret)
    {
        var target = Analyze(sqlWithCaret);
        Assert.NotNull(target);
        return target!;
    }

    /// <summary>把來源攤平成「限定字→欄位名稱」的字串，方便一次比對整個結果。</summary>
    private static string[] Names(SqlWildcardTarget target)
    {
        return target.Sources
            .SelectMany(source => source.Kind == SqlColumnSourceKind.Table
                ? new[] { $"{source.Qualifier}:表 {source.Table!.ObjectName}" }
                : source.Names.Select(name => $"{source.Qualifier}:{name}"))
            .ToArray();
    }

    [Fact]
    public void 資料表的萬用字元可以展開()
    {
        var target = Expand("SELECT *| FROM dbo.PUBLISHER");
        var source = Assert.Single(target.Sources);

        Assert.Equal(SqlColumnSourceKind.Table, source.Kind);
        Assert.Equal("PUBLISHER", source.Table!.ObjectName);
        Assert.Equal("dbo", source.Table.SchemaName);
        Assert.Equal("PUBLISHER", source.Qualifier);

        // 只有 * 那一個字元會被換掉。
        Assert.Equal("SELECT ".Length, target.Start);
        Assert.Equal(1, target.Length);
        Assert.Null(target.QualifierText);
    }

    /// <remarks>
    /// 單一資料來源不加限定字：欄位名稱不可能模稜兩可，補上去只是雜訊。
    /// </remarks>
    [Fact]
    public void 只有一個資料來源時不加限定字()
    {
        Assert.False(Expand("SELECT *| FROM dbo.PUBLISHER").Qualify);
    }

    [Fact]
    public void 兩個資料來源時補上別名()
    {
        var target = Expand("SELECT *| FROM dbo.Loans o JOIN dbo.Publisher c ON o.Id = c.Id");

        Assert.True(target.Qualify);
        Assert.Equal(new[] { "o:表 Loans", "c:表 Publisher" }, Names(target));
    }

    [Fact]
    public void 限定過的萬用字元只展開該來源()
    {
        var target = Expand("SELECT o.*| FROM dbo.Loans o JOIN dbo.Publisher c ON o.Id = c.Id");
        var source = Assert.Single(target.Sources);

        Assert.Equal("Loans", source.Table!.ObjectName);
        Assert.Equal("o", target.QualifierText);
        Assert.True(target.Qualify);
        Assert.Equal("SELECT ".Length, target.Start);
        Assert.Equal("o.*".Length, target.Length);
    }

    /// <remarks>
    /// 保留原文而不是解析後的名稱：把使用者寫的 dbo.PUBLISHER 改寫成 PUBLISHER
    /// 雖然也合法，卻是他沒有要求的改動。
    /// </remarks>
    [Fact]
    public void 多段限定字整段保留()
    {
        var target = Expand("SELECT dbo.PUBLISHER.*| FROM dbo.PUBLISHER");

        Assert.Equal("dbo.PUBLISHER", target.QualifierText);
        Assert.Equal("SELECT ".Length, target.Start);
        Assert.Equal("dbo.PUBLISHER.*".Length, target.Length);
    }

    [Fact]
    public void 逗號後面的萬用字元也算()
    {
        var target = Expand("SELECT GETDATE(), *| FROM dbo.PUBLISHER");

        Assert.Equal("PUBLISHER", Assert.Single(target.Sources).Table!.ObjectName);
    }

    [Theory]
    [InlineData("SELECT TOP 10 *| FROM dbo.PUBLISHER")]
    [InlineData("SELECT TOP (10) *| FROM dbo.PUBLISHER")]
    [InlineData("SELECT TOP @n *| FROM dbo.PUBLISHER")]
    [InlineData("SELECT DISTINCT *| FROM dbo.PUBLISHER")]
    [InlineData("SELECT TOP 10 PERCENT WITH TIES *| FROM dbo.PUBLISHER")]
    public void 選取清單的前置詞不影響判斷(string sql)
    {
        Assert.NotNull(Analyze(sql));
    }

    /// <summary>星號在 T-SQL 裡絕大多數時候是乘號。</summary>
    [Theory]
    [InlineData("SELECT COUNT(*|) FROM dbo.PUBLISHER")]
    [InlineData("SELECT 5 *| 3 FROM dbo.PUBLISHER")]
    [InlineData("SELECT a *| b FROM dbo.PUBLISHER")]
    [InlineData("SELECT Price FROM t WHERE Price = 10 *| 2")]
    [InlineData("INSERT INTO t (a, *|)")]
    [InlineData("SELECT * FROM t ORDER BY a, *|")]
    public void 乘號與其他位置的星號不展開(string sql)
    {
        Assert.Null(Analyze(sql));
    }

    [Fact]
    public void 沒有資料來源就不展開()
    {
        Assert.Null(Analyze("SELECT *|"));
    }

    [Fact]
    public void 游標不在星號後面就不展開()
    {
        Assert.Null(Analyze("SELECT * |FROM dbo.PUBLISHER"));
    }

    [Fact]
    public void 註解與字串裡的星號不展開()
    {
        Assert.Null(Analyze("SELECT '*|' FROM dbo.PUBLISHER"));
        Assert.Null(Analyze("SELECT 1 FROM dbo.PUBLISHER -- *|"));
    }

    /// <remarks>
    /// 沒有宣告在這份指令碼裡的資料表變數，欄位既不在指令碼裡也不在中繼資料裡，
    /// 只能整個放棄——少了幾個欄位的 SELECT 仍然執行得動，卻執行出錯的結果。
    /// </remarks>
    [Fact]
    public void 找不到宣告的資料表變數不展開()
    {
        Assert.Null(Analyze("SELECT *| FROM @rows r"));
    }

    /// <summary>
    /// 指令碼自己宣告的資料表展得開。
    /// </summary>
    /// <remarks>
    /// 資料表變數不是 <c>sys.objects</c> 裡的物件，暫存資料表在 tempdb 裡，
    /// 兩者的欄位中繼資料一列都查不到——但 <c>DECLARE @rows TABLE (…)</c> 與
    /// <c>CREATE TABLE #Loan (…)</c> 就寫在上面幾行，讀得出來。
    /// </remarks>
    [Theory]
    [InlineData("DECLARE @rows TABLE (Id INT, CopyNo NVARCHAR(20));\r\nSELECT *| FROM @rows")]
    [InlineData("DECLARE @rows TABLE (Id INT, CopyNo NVARCHAR(20));\r\nSELECT r.*| FROM @rows r")]
    [InlineData("CREATE TABLE #Loan (Id INT, CopyNo NVARCHAR(20));\r\nSELECT *| FROM #Loan")]
    [InlineData("CREATE TABLE #Loan (Id INT, CopyNo NVARCHAR(20));\r\nSELECT *| FROM [#Loan]")]
    public void 指令碼宣告的資料表展得開(string sqlWithCaret)
    {
        var source = Assert.Single(Expand(sqlWithCaret).Sources);

        Assert.Equal(SqlColumnSourceKind.Names, source.Kind);
        Assert.Equal(new[] { "Id", "CopyNo" }, source.Names);
    }

    [Fact]
    public void 衍生資料表讀它自己的選取清單()
    {
        var target = Expand("SELECT *| FROM (SELECT Id, Name FROM dbo.PUBLISHER) d");

        Assert.Equal(new[] { "d:Id", "d:Name" }, Names(target));
        Assert.False(target.Qualify);
    }

    [Fact]
    public void 衍生資料表裡的星號往內遞迴()
    {
        var target = Expand("SELECT *| FROM (SELECT * FROM dbo.PUBLISHER c) d");
        var source = Assert.Single(target.Sources);

        Assert.Equal("PUBLISHER", source.Table!.ObjectName);

        // 限定字是最外層的 d，內層的 c 在外面根本不存在。
        Assert.Equal("d", source.Qualifier);
    }

    [Fact]
    public void 衍生資料表可以混合名稱與星號()
    {
        var target = Expand("SELECT *| FROM (SELECT Id, * FROM dbo.PUBLISHER) d");

        Assert.Equal(new[] { "d:Id", "d:表 PUBLISHER" }, Names(target));
    }

    /// <summary>
    /// 資料表提示不會讓 <c>SELECT *</c> 少展開一個來源。
    /// </summary>
    /// <remarks>
    /// 剖析若停在 <c>(NOLOCK)</c> 前面，後面那個逗號就不再是來源清單的逗號，
    /// <c>dbo.Copy</c> 整個消失——而少了一半欄位的 <c>SELECT</c> 仍然執行得動，
    /// 只是執行出錯的結果。這一種比展不開糟得多，因為看不出少了東西。
    /// </remarks>
    [Theory]
    [InlineData("SELECT *| FROM dbo.Loan l (NOLOCK), dbo.Copy c")]
    [InlineData("SELECT *| FROM dbo.Loan l WITH (NOLOCK), dbo.Copy c")]
    [InlineData("SELECT *| FROM dbo.Loan l TABLESAMPLE (10 PERCENT), dbo.Copy c")]
    public void 資料表提示不會造成部分展開(string sqlWithCaret)
    {
        var target = Expand(sqlWithCaret);

        Assert.Equal(new[] { "l:表 Loan", "c:表 Copy" }, Names(target));
        Assert.True(target.Qualify);
    }

    /// <summary>
    /// <c>SELECT … INTO #Temp FROM dbo.fn(…)</c> 的星號追得到資料表值函式。
    /// </summary>
    /// <remarks>
    /// 與 QuickInfo／預覽刻意不同：那條路徑不等查詢，遇到要問中繼資料的來源就整份
    /// 放棄，所以 <c>#Temp</c> 停在那裡什麼都不顯示。Tab 展開等得起，於是往下追到
    /// 函式那一層。兩個答案不是各寫一份分岔出來的——中間那份遞迴只有一份，
    /// 差別只在呼叫端付不付得起一次查詢。
    /// </remarks>
    [Fact]
    public void 投影暫存表的星號追到資料表值函式()
    {
        var target = Expand(
            "SELECT * INTO #Temp FROM dbo.fn_LoansByReader(0) f (NOLOCK); SELECT *| FROM #Temp");
        var source = Assert.Single(target.Sources);

        Assert.Equal("fn_LoansByReader", source.Table!.ObjectName);
        Assert.Equal("#Temp", source.Qualifier);
    }

    /// <summary>別名後面寫出來的資料行清單覆寫主體，與 CTE 同一條規則。</summary>
    /// <remarks>
    /// 資料表值建構式只有這一條路：<c>VALUES</c> 不是 <c>SELECT</c>，
    /// 主體一個名稱都讀不出來，少了這份清單同一個星號就展不開。
    /// </remarks>
    [Fact]
    public void 衍生資料表的資料行清單優先()
    {
        var target = Expand("SELECT T.*| FROM (VALUES (1, N'Alice')) AS T (Code, Name)");

        Assert.Equal(new[] { "T:Code", "T:Name" }, Names(target));
    }

    [Fact]
    public void 讀得出三種欄位別名寫法()
    {
        var target = Expand(
            "SELECT *| FROM (SELECT Id AS Code, Total = Qty * Price, ISNULL(Memo, '') Note FROM dbo.O) d");

        Assert.Equal(new[] { "d:Code", "d:Total", "d:Note" }, Names(target));
    }

    [Fact]
    public void 沒有名稱的運算式不展開()
    {
        Assert.Null(Analyze("SELECT *| FROM (SELECT Qty * Price FROM dbo.O) d"));
    }

    [Fact]
    public void CTE讀它主體的選取清單()
    {
        var target = Expand("WITH c AS (SELECT Id, Name FROM dbo.PUBLISHER) SELECT *| FROM c");

        Assert.Equal(new[] { "c:Id", "c:Name" }, Names(target));
    }

    /// <remarks>明確寫出的資料行清單會覆寫主體算出來的名稱。</remarks>
    [Fact]
    public void CTE的資料行清單優先()
    {
        var target = Expand("WITH c (A, B) AS (SELECT Id, Name FROM dbo.PUBLISHER) SELECT *| FROM c");

        Assert.Equal(new[] { "c:A", "c:B" }, Names(target));
    }

    [Fact]
    public void CTE主體的星號往內遞迴()
    {
        var target = Expand("WITH c AS (SELECT * FROM dbo.PUBLISHER) SELECT *| FROM c");
        var source = Assert.Single(target.Sources);

        Assert.Equal("PUBLISHER", source.Table!.ObjectName);
        Assert.Equal("c", source.Qualifier);
    }

    [Fact]
    public void 逗號串起來的多個CTE都認得()
    {
        var target = Expand(
            "WITH a AS (SELECT Id FROM dbo.A), b AS (SELECT Name FROM dbo.B) SELECT *| FROM a JOIN b ON 1 = 1");

        Assert.Equal(new[] { "a:Id", "b:Name" }, Names(target));
    }

    /// <remarks>遞迴 CTE 的第一段決定欄位名稱，UNION ALL 之後的那一段不必看。</remarks>
    [Fact]
    public void 遞迴CTE取第一段的欄位名稱()
    {
        var target = Expand(
            "WITH c AS (SELECT Id, Lv FROM dbo.T UNION ALL SELECT t.Id, c.Lv FROM dbo.T t JOIN c ON 1 = 1) " +
            "SELECT *| FROM c");

        Assert.Equal(new[] { "c:Id", "c:Lv" }, Names(target));
    }

    [Fact]
    public void 直接參照自己的CTE不展開()
    {
        Assert.Null(Analyze("WITH c AS (SELECT * FROM c) SELECT *| FROM c"));
    }

    /// <remarks>
    /// 帶結構描述的名稱一定是資料庫裡的物件；CTE 名稱沒有結構描述。
    /// </remarks>
    [Fact]
    public void 帶結構描述的名稱不會誤認成CTE()
    {
        var target = Expand("WITH c AS (SELECT Id FROM dbo.A) SELECT *| FROM dbo.c");
        var source = Assert.Single(target.Sources);

        Assert.Equal(SqlColumnSourceKind.Table, source.Kind);
    }

    /// <remarks>WITH (NOLOCK) 後面接的是左括號而不是名稱，自然不會被當成 CTE。</remarks>
    [Fact]
    public void 資料表提示不會被當成CTE()
    {
        var target = Expand("SELECT *| FROM dbo.PUBLISHER WITH (NOLOCK)");

        Assert.Equal("PUBLISHER", Assert.Single(target.Sources).Table!.ObjectName);
    }

    /// <remarks>子查詢的 FROM 子句屬於它自己，外層看不到。</remarks>
    [Fact]
    public void 外層看不到子查詢裡的資料表()
    {
        var target = Expand("SELECT *| FROM dbo.A a WHERE a.Id IN (SELECT b.Id FROM dbo.B b)");

        Assert.Equal(new[] { "a:表 A" }, Names(target));
        Assert.False(target.Qualify);
    }

    [Fact]
    public void 子查詢裡的星號用子查詢自己的資料來源()
    {
        var target = Expand("SELECT Id FROM dbo.A a WHERE EXISTS (SELECT *| FROM dbo.B b)");

        Assert.Equal(new[] { "b:表 B" }, Names(target));
    }

    /// <summary>
    /// 資料表值函式是一般的資料來源，欄位由中繼資料給。
    /// </summary>
    /// <remarks>
    /// 引數清單不影響它是什麼：這裡攤平出來的仍然是一個資料表來源，
    /// 而它的資料行在 <c>sys.columns</c> 裡查得到
    /// （<c>SqlObjectKinds.HasCatalogColumns</c>）。
    /// </remarks>
    [Fact]
    public void 資料表值函式的萬用字元可以展開()
    {
        var target = Expand("SELECT *| FROM dbo.fn_LoansByReader(0) f");
        var source = Assert.Single(target.Sources);

        Assert.Equal(SqlColumnSourceKind.Table, source.Kind);
        Assert.Equal("fn_LoansByReader", source.Table!.ObjectName);
        Assert.Equal("dbo", source.Table.SchemaName);
        Assert.Equal("f", source.Qualifier);
    }

    /// <remarks>
    /// 引數裡的逗號不是資料來源清單的逗號。認錯的話會多出一個叫
    /// <c>N'x'</c> 的來源，而 <c>ResolveAll</c> 只要有一個解析不出來就整個不展開
    /// ——症狀是這一句的 <c>SELECT *</c> 安靜地沒有反應。
    /// </remarks>
    [Fact]
    public void 資料表值函式的引數逗號不算資料來源清單()
    {
        var target = Expand("SELECT *| FROM dbo.fn_LoansByReader(0, N'x') f");

        Assert.Equal(new[] { "f:表 fn_LoansByReader" }, Names(target));
        Assert.False(target.Qualify);
    }

    /// <remarks>資料表與資料表值函式併用時，兩個來源都要在。</remarks>
    [Fact]
    public void APPLY接上的資料表值函式也一起展開()
    {
        var target = Expand("SELECT *| FROM dbo.Loan l CROSS APPLY dbo.fn_LoansByReader(l.CopyNo) f");

        Assert.Equal(new[] { "l:表 Loan", "f:表 fn_LoansByReader" }, Names(target));
        Assert.True(target.Qualify);
    }

    /// <summary>
    /// 展開寫回去的是欄位名稱，拿錯資料庫就是把別人的欄位貼進指令碼裡，
    /// 因此展開器要看得到這份指令碼換過資料庫。
    /// </summary>
    [Fact]
    public void 帶著指令碼換過的資料庫()
    {
        var target = Analyze("USE LibArchive; SELECT *| FROM dbo.PUBLISHER");

        Assert.NotNull(target);
        Assert.Equal("LibArchive", target!.DatabaseSwitch);
    }
}
