using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 限定字後方的欄位建議：<c>u.</c> 要能解析成 <c>u</c> 給得出的欄位。
/// </summary>
public sealed class SqlColumnCompletionTests
{
    private static SqlCompletionContext Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);
    }

    /// <summary>限定字解析成的那一張資料表；來源不只一個或不是資料表時就是失敗。</summary>
    private static SqlTableReference ResolvedTable(SqlCompletionContext context)
    {
        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.NotNull(context.ColumnSources);

        var source = Assert.Single(context.ColumnSources!);

        Assert.Equal(SqlColumnSourceKind.Table, source.Kind);
        return source.Table!;
    }

    /// <summary>把來源攤平成字串，資料表寫成「表 名稱」，方便一次比對整份結果。</summary>
    private static string[] Columns(SqlCompletionContext context)
    {
        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.NotNull(context.ColumnSources);

        return context.ColumnSources!
            .SelectMany(source => source.Kind == SqlColumnSourceKind.Table
                ? new[] { $"表 {source.Table!.ObjectName}" }
                : source.Names)
            .ToArray();
    }

    /// <remarks>
    /// MERGE 的動作子句過去解析不出 target 與 source：<c>WHEN MATCHED THEN UPDATE</c>
    /// 裡的 UPDATE 被當成新敘述的開頭，範圍就從那裡切斷了（見
    /// <c>SqlScopeAnalyzer.IsMergeAction</c>）。症狀是 Snippet 的 <c>mg</c> 走到
    /// 第三格之後，每一格都不再有清單。
    /// </remarks>
    [Fact]
    public void MERGE的UPDATE子句解析得出target()
    {
        var table = ResolvedTable(Analyze("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN MATCHED THEN\n    UPDATE SET target.| = source.CopyNo"));

        Assert.Equal("Loan", table.ObjectName);
    }

    [Fact]
    public void MERGE的UPDATE子句解析得出source()
    {
        var table = ResolvedTable(Analyze("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN MATCHED THEN\n    UPDATE SET target.CopyNo = source.|"));

        Assert.Equal("LoanDetail", table.ObjectName);
    }

    [Fact]
    public void MERGE的VALUES子句解析得出source()
    {
        var table = ResolvedTable(
            Analyze("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN NOT MATCHED BY TARGET THEN\n    INSERT (CopyNo)\n    VALUES (source.|)"));

        Assert.Equal("LoanDetail", table.ObjectName);
    }

    /// <summary>
    /// <c>INSERT (C|)</c> 沒有限定字，列的是敘述看得到的來源。
    /// </summary>
    /// <remarks>
    /// 那裡文法上只該有 target 的欄位，但範圍解析給的是整個 MERGE 的兩張表，
    /// 收斂成一張要另外記住「這個括號屬於 INSERT 子句」。多幾個選不中的名稱是
    /// 多按幾下，而兩張表都不列的話那一格就完全沒有補字——<c>mg</c> 過去正是如此。
    ///
    /// 要有前綴才成立：那個位置推不出目標，空前綴時整份不參與，
    /// 與 <c>SELECT |</c> 是同一條規則。
    /// </remarks>
    [Fact]
    public void MERGE的INSERT欄位清單看得到兩張表()
    {
        var context = Analyze("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN NOT MATCHED BY TARGET THEN\n    INSERT (C|)");

        Assert.True(context.IsValid);
        Assert.Null(context.Qualifier);
        Assert.Equal("C", context.Prefix);
        Assert.Equal(
            new[] { "Loan", "LoanDetail" },
            context.ScopeSources.Select(source => source.Table!.ObjectName).ToArray());
    }

    [Fact]
    public void 別名限定字解析成欄位目標()
    {
        var table = ResolvedTable(Analyze("SELECT u.| FROM dbo.Lib_Reader u"));

        Assert.Equal("Lib_Reader", table.ObjectName);
        Assert.Equal("dbo", table.SchemaName);
    }

    /// <summary>
    /// 這是只看游標前文永遠做不到的事：FROM 子句整個在游標後面。
    /// </summary>
    [Fact]
    public void FROM子句在游標後方仍可解析()
    {
        var before = SqlCompletionContextAnalyzer.Analyze("SELECT u.");

        Assert.NotEqual(CompletionTarget.Column, before.Target);
        Assert.Equal(CompletionTarget.Column, Analyze("SELECT u.| FROM dbo.Lib_Reader u").Target);
    }

    [Fact]
    public void 已輸入前綴時仍是欄位目標()
    {
        var context = Analyze("SELECT u.Nam| FROM dbo.Lib_Reader u");

        Assert.Equal("Nam", context.Prefix);
        Assert.Equal("Lib_Reader", ResolvedTable(context).ObjectName);
    }

    [Fact]
    public void 沒有別名時用資料表名稱限定()
    {
        Assert.Equal("Lib_Reader", ResolvedTable(Analyze("SELECT Lib_Reader.| FROM dbo.Lib_Reader")).ObjectName);
    }

    [Fact]
    public void JOIN的兩個別名都能解析()
    {
        const string sql = "SELECT o.Id, c.| FROM dbo.Loans o JOIN dbo.Publisher c ON o.PublisherId = c.Id";

        Assert.Equal("Publisher", ResolvedTable(Analyze(sql)).ObjectName);
    }

    [Fact]
    public void 方括號別名可解析()
    {
        Assert.Equal("Lib_Reader", ResolvedTable(Analyze("SELECT [u x].| FROM dbo.Lib_Reader AS [u x]")).ObjectName);
    }

    /// <summary>
    /// 結構描述限定字不是資料來源，必須維持原本列出該結構描述物件的行為。
    /// </summary>
    [Fact]
    public void 結構描述限定字不會被誤判成欄位()
    {
        var context = Analyze("SELECT * FROM dbo.| ");

        Assert.Equal(CompletionTarget.DataSource, context.Target);
        Assert.Equal("dbo", context.Qualifier);
        Assert.Null(context.ColumnSources);
    }

    /// <summary>
    /// 資料表變數的欄位既不在指令碼裡也不在中繼資料裡，不能宣稱解析成功。
    /// </summary>
    [Fact]
    public void 資料表變數不改成欄位目標()
    {
        var context = Analyze("SELECT r.| FROM @rows r");

        Assert.NotEqual(CompletionTarget.Column, context.Target);
        Assert.Null(context.ColumnSources);
    }

    [Fact]
    public void 解析不到的限定字維持結構描述解讀()
    {
        var context = Analyze("SELECT zzz.| FROM dbo.Lib_Reader u");

        Assert.Equal("zzz", context.Qualifier);
        Assert.Null(context.ColumnSources);
    }

    /// <summary>子查詢內的別名不能洩漏到外層。</summary>
    [Fact]
    public void 外層看不到子查詢的別名()
    {
        Assert.Null(Analyze("SELECT i.| FROM (SELECT X FROM dbo.Copy i) d").ColumnSources);
    }

    /// <summary>
    /// 實機回報的兩個情形：大寫資料表名稱、以及 JOIN 之後在 ON 條件裡用第二個別名。
    /// </summary>
    [Theory]
    [InlineData("SELECT u.| FROM PUBLISHER u", "PUBLISHER")]
    [InlineData("SELECT u.s| FROM PUBLISHER u", "PUBLISHER")]
    [InlineData(
        "SELECT u.* FROM PUBLISHER u INNER JOIN Cat_BookCopy b ON b.|",
        "Cat_BookCopy")]
    [InlineData(
        "SELECT u.* FROM PUBLISHER u INNER JOIN Cat_BookCopy b ON b.Id = u.|",
        "PUBLISHER")]
    public void 實機情形(string sqlWithCaret, string expectedTable)
    {
        Assert.Equal(expectedTable, ResolvedTable(Analyze(sqlWithCaret)).ObjectName);
    }

    [Fact]
    public void 字串與註解內不建議欄位()
    {
        Assert.False(Analyze("SELECT 'u.|' FROM dbo.Lib_Reader u").IsValid);
        Assert.False(Analyze("-- u.|\r\nSELECT * FROM dbo.Lib_Reader u").IsValid);
    }

    /// <summary>
    /// 括號裡的別名一樣解析得出來。
    /// </summary>
    /// <remarks>
    /// 實機回報：<c>SELECT COUNT(a.| FROM dbo.PUBLISHER a</c> 沒有任何建議。
    /// 原因是範圍分析器把每一個左括號都當成子查詢，括號裡看不到外層的 FROM 子句，
    /// 別名解析不出來就退回結構描述解讀，而沒有一個物件屬於名為 <c>a</c> 的
    /// 結構描述——清單於是完全是空的。
    /// </remarks>
    [Theory]
    [InlineData("SELECT COUNT(u.|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT COUNT(u.| FROM dbo.Lib_Reader u")]
    [InlineData("SELECT SUM(u.Amount), MAX(u.|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT COUNT(DISTINCT u.|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT ISNULL(u.|, 0) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT ISNULL(SUM(CONVERT(int, u.|)), 0) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u WHERE (u.| = 1)")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u WHERE Id IN (u.|)")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u ORDER BY DATEPART(day, u.|)")]
    public void 括號內仍解析得出別名(string sqlWithCaret)
    {
        Assert.Equal("Lib_Reader", ResolvedTable(Analyze(sqlWithCaret)).ObjectName);
    }

    /// <summary>
    /// 子查詢仍然自成範圍。
    /// </summary>
    /// <remarks>
    /// 規則的另一半：分不出「開啟查詢的括號」與「運算式的括號」的話，
    /// 修好彙總函式就會弄壞子查詢，內層的別名會解析到外層的資料表。
    /// </remarks>
    [Fact]
    public void 子查詢內的別名仍指向子查詢的資料表()
    {
        var context = Analyze(
            "SELECT * FROM dbo.Lib_Reader u WHERE Id IN (SELECT c.| FROM dbo.Lib_Shelf c)");

        Assert.Equal("Lib_Shelf", ResolvedTable(context).ObjectName);
    }

    /// <summary>
    /// 衍生資料表與 CTE 的欄位就寫在指令碼裡，讀得出來就該列出來。
    /// </summary>
    /// <remarks>
    /// 實機回報：同一段 SQL 的 <c>a.*</c> 按 Tab 展得開，<c>a.</c> 卻一個建議都沒有。
    /// 兩邊各有一份「別名指向哪些欄位」的解析，只有萬用字元那一份會往子查詢裡看。
    /// 現在共用 <see cref="SqlColumnSourceResolver"/>，答案不可能再分岔。
    /// </remarks>
    [Fact]
    public void 衍生資料表的別名列出它的選取清單()
    {
        var context = Analyze(
            "SELECT a.| FROM (SELECT a.PUBL_CODE, a.SHELF_LOCATION_CODE, b.CopyNo " +
            "FROM dbo.PUBLISHER a INNER JOIN dbo.Cat_BookCopy b ON b.PublCode = a.PUBL_CODE) a");

        Assert.Equal(new[] { "PUBL_CODE", "SHELF_LOCATION_CODE", "CopyNo" }, Columns(context));
    }

    /// <summary>衍生資料表的別名在 WHERE 子句裡一樣算數。</summary>
    [Fact]
    public void 衍生資料表的別名在WHERE子句可用()
    {
        var context = Analyze("SELECT * FROM (SELECT c.Id AS Code FROM dbo.PUBLISHER c) a WHERE a.|");

        Assert.Equal(new[] { "Code" }, Columns(context));
    }

    [Fact]
    public void CTE的別名列出主體的選取清單()
    {
        var context = Analyze(
            ";WITH CTE_TEST AS (SELECT a.PUBL_CODE FROM dbo.PUBLISHER a) SELECT TOP (1) a.| FROM CTE_TEST a");

        Assert.Equal(new[] { "PUBL_CODE" }, Columns(context));
    }

    /// <summary>寫出來的資料行清單會覆寫主體算出來的名稱。</summary>
    [Fact]
    public void CTE的資料行清單優先()
    {
        var context = Analyze(
            ";WITH c (Code, Name) AS (SELECT Id, Title FROM dbo.Copy) SELECT x.| FROM c x");

        Assert.Equal(new[] { "Code", "Name" }, Columns(context));
    }

    /// <summary>
    /// 衍生資料表裡的 <c>*</c> 要遞迴到底層的資料表，欄位才問得到中繼資料。
    /// </summary>
    [Fact]
    public void 衍生資料表裡的萬用字元遞迴到底層資料表()
    {
        var context = Analyze("SELECT d.| FROM (SELECT Id, * FROM dbo.PUBLISHER c) d");

        Assert.Equal(new[] { "Id", "表 PUBLISHER" }, Columns(context));
    }

    /// <summary>
    /// 帶結構描述的名稱一定是資料庫裡的物件，不會是 CTE。
    /// </summary>
    [Fact]
    public void 帶結構描述的名稱不查CTE名冊()
    {
        var context = Analyze(
            ";WITH PUBLISHER AS (SELECT Id FROM dbo.Copy) SELECT a.| FROM dbo.PUBLISHER a");

        Assert.Equal("PUBLISHER", ResolvedTable(context).ObjectName);
    }

    /// <summary>
    /// 沒有名稱的運算式在外層無從稱呼，整個來源就此放棄。
    /// </summary>
    /// <remarks>
    /// 與展開 <c>SELECT *</c> 同一條規則：只列得出一半的欄位，比什麼都不列更難發現
    /// 少了東西。退回結構描述解讀後至少還看得到物件清單。
    /// </remarks>
    [Fact]
    public void 選取清單有無名運算式時不改成欄位目標()
    {
        Assert.Null(Analyze("SELECT d.| FROM (SELECT Qty * Price FROM dbo.Copy) d").ColumnSources);
    }

    /// <summary>直接參照自己的 CTE 不能讓解析一路展開下去。</summary>
    [Fact]
    public void 參照自己的CTE整個放棄()
    {
        Assert.Null(Analyze(";WITH c AS (SELECT * FROM c) SELECT x.| FROM c x").ColumnSources);
    }

    /// <summary>
    /// 沒有限定字的位置要列出敘述看得到的欄位，子查詢與 CTE 也算在內。
    /// </summary>
    [Fact]
    public void 沒有限定字時也讀得出敘述的欄位來源()
    {
        var context = Analyze("SELECT cu| FROM (SELECT c.PUBL_CODE FROM dbo.PUBLISHER c) a JOIN dbo.Copy i ON 1 = 1");

        Assert.Null(context.ColumnSources);
        Assert.Equal(
            new[] { "a:PUBL_CODE", "i:表 Copy" },
            context.ScopeSources
                .SelectMany(source => source.Kind == SqlColumnSourceKind.Table
                    ? new[] { $"{source.Qualifier}:表 {source.Table!.ObjectName}" }
                    : source.Names.Select(name => $"{source.Qualifier}:{name}"))
                .ToArray());
    }

    /// <summary>解析不出來的來源跳過，其他來源的欄位照樣列。</summary>
    [Fact]
    public void 敘述的欄位來源跳過解析不出來的那一個()
    {
        var context = Analyze("SELECT cu| FROM @rows r JOIN dbo.Copy i ON 1 = 1");

        var source = Assert.Single(context.ScopeSources);

        Assert.Equal("Copy", source.Table!.ObjectName);
    }

    /// <summary>
    /// 資料表值函式的別名要解析成那個函式，而不是被引數的括號吃掉。
    /// </summary>
    /// <remarks>
    /// 它的資料行由中繼資料給（<c>SqlObjectKinds.HasCatalogColumns</c>），
    /// 因此這裡要的是一個 <see cref="SqlColumnSourceKind.Table"/> 來源——
    /// 攤平成別的東西、或者根本沒有來源，<c>f.</c> 都是一個欄位都列不出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT f.| FROM dbo.fn_LoansByReader(0) f")]
    [InlineData("SELECT f.| FROM dbo.fn_LoansByReader(@ReaderId, N'x') AS f")]
    [InlineData("SELECT f.| FROM dbo.Loan l CROSS APPLY dbo.fn_LoansByReader(l.CopyNo) f")]
    public void 資料表值函式的別名解析成該函式(string sqlWithCaret)
    {
        var table = ResolvedTable(Analyze(sqlWithCaret));

        Assert.Equal("dbo", table.SchemaName);
        Assert.Equal("fn_LoansByReader", table.ObjectName);
        Assert.False(table.IsDerived);
    }

    /// <remarks>
    /// 引數本身是一次函式呼叫時括號是巢狀的；只跳一層的話後面的別名會被當成
    /// 資料來源的一部分，那個別名就再也解析不出來。
    /// </remarks>
    [Fact]
    public void 引數裡還有函式呼叫時別名仍解析得出來()
    {
        var table = ResolvedTable(
            Analyze("SELECT f.| FROM dbo.fn_LoansByReader(dbo.fn_DueDate(GETDATE(), 14)) f"));

        Assert.Equal("fn_LoansByReader", table.ObjectName);
    }

    /// <summary>沒有別名時用函式名稱限定，與資料表同一條規則。</summary>
    [Fact]
    public void 沒有別名的資料表值函式用函式名稱限定()
    {
        var table = ResolvedTable(Analyze("SELECT fn_LoansByReader.| FROM dbo.fn_LoansByReader(0)"));

        Assert.Equal("fn_LoansByReader", table.ObjectName);
    }
}
