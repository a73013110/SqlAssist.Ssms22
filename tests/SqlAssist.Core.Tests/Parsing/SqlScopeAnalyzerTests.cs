using System.Linq;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlScopeAnalyzerTests
{
    private static SqlStatementScope Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlScopeAnalyzer.Analyze(input.Text, input.Caret);
    }

    /// <remarks>
    /// <c>WHEN MATCHED THEN UPDATE</c>、<c>WHEN NOT MATCHED THEN INSERT</c> 裡的
    /// <c>UPDATE</c>／<c>INSERT</c> 屬於同一個 MERGE，不是新敘述的開頭。把它們當成
    /// 邊界的話，游標一進到 <c>WHEN</c> 之後，MERGE 的 target 與 source 兩個別名
    /// 就全部解析不出來——症狀是 <c>target.|</c> 與 <c>source.|</c> 都不再列欄位。
    /// </remarks>
    [Theory]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.| = source.CopyNo")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN MATCHED THEN\n    UPDATE SET target.| = source.CopyNo")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN MATCHED THEN\n    UPDATE SET target.CopyNo = source.|")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN NOT MATCHED BY TARGET THEN\n    INSERT (|)")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN NOT MATCHED BY TARGET THEN\n    INSERT (CopyNo)\n    VALUES (source.|)")]
    public void MERGE的動作子句仍看得到target與source(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.Equal(2, scope.Tables.Count);
        Assert.True(scope.TryResolve("target", out var target));
        Assert.Equal("Loan", target.ObjectName);
        Assert.True(scope.TryResolve("source", out var source));
        Assert.Equal("LoanDetail", source.ObjectName);
    }

    /// <summary>THEN 之外的 UPDATE 與 INSERT 仍然是敘述邊界。</summary>
    /// <remarks>
    /// 認的是「前一個詞元是不是 THEN」，不是「這份指令碼裡有沒有 MERGE」。
    /// 一個 MERGE 之後接著獨立的 UPDATE，那個 UPDATE 必須切斷範圍，
    /// 否則它會看到上一句 MERGE 的兩張表。
    /// </remarks>
    [Fact]
    public void MERGE之後獨立的UPDATE仍是新敘述()
    {
        var scope = Analyze(
            "MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.CopyNo\nWHEN MATCHED THEN" +
            "\n    UPDATE SET target.CopyNo = source.CopyNo;\n\nUPDATE dbo.Branch SET x = |");
        var table = Assert.Single(scope.Tables);

        Assert.Equal("Branch", table.ObjectName);
    }

    /// <summary>CASE 的 THEN 後面是運算式，不會撞上這條規則。</summary>
    [Fact]
    public void CASE的THEN不會讓後面的敘述併進來()
    {
        var scope = Analyze(
            "SELECT CASE WHEN 1 = 1 THEN 'x' ELSE 'y' END FROM dbo.Loan;\nUPDATE dbo.Branch SET x = |");
        var table = Assert.Single(scope.Tables);

        Assert.Equal("Branch", table.ObjectName);
    }

    [Fact]
    public void 取得單一資料表與別名()
    {
        var scope = Analyze("SELECT | FROM dbo.Lib_Reader AS u");
        var table = Assert.Single(scope.Tables);

        Assert.Equal("dbo", table.SchemaName);
        Assert.Equal("Lib_Reader", table.ObjectName);
        Assert.Equal("u", table.Alias);
    }

    [Fact]
    public void 別名可以省略AS()
    {
        var table = Assert.Single(Analyze("SELECT | FROM dbo.Lib_Reader u").Tables);

        Assert.Equal("Lib_Reader", table.ObjectName);
        Assert.Equal("u", table.Alias);
    }

    [Fact]
    public void 沒有別名時以物件名稱限定()
    {
        var table = Assert.Single(Analyze("SELECT | FROM Lib_Reader").Tables);

        Assert.Null(table.Alias);
        Assert.Equal("Lib_Reader", table.EffectiveName);
    }

    /// <summary>WHERE 是子句關鍵字，不能被當成別名吃掉。</summary>
    [Fact]
    public void 子句關鍵字不會被當成別名()
    {
        var table = Assert.Single(Analyze("SELECT * FROM Lib_Reader WHERE |").Tables);

        Assert.Null(table.Alias);
        Assert.Equal("Lib_Reader", table.ObjectName);
    }

    [Fact]
    public void 取得JOIN的所有資料表()
    {
        var scope = Analyze(
            "SELECT | FROM dbo.Loans o INNER JOIN dbo.Publisher c ON o.PublisherId = c.Id");

        Assert.Equal(new[] { "Loans", "Publisher" }, scope.Tables.Select(t => t.ObjectName));
        Assert.Equal(new[] { "o", "c" }, scope.Tables.Select(t => t.Alias));
    }

    [Fact]
    public void 取得逗號分隔的資料表清單()
    {
        var scope = Analyze("SELECT | FROM A a, B b, dbo.C c");

        Assert.Equal(new[] { "A", "B", "C" }, scope.Tables.Select(t => t.ObjectName));
        Assert.Equal(new[] { "a", "b", "c" }, scope.Tables.Select(t => t.Alias));
    }

    [Fact]
    public void 略過資料表提示後仍讀得到別名()
    {
        var table = Assert.Single(Analyze("SELECT * FROM Loans WITH (NOLOCK) o WHERE |").Tables);

        Assert.Equal("Loans", table.ObjectName);
        Assert.Equal("o", table.Alias);
    }

    [Fact]
    public void 支援方括號名稱與別名()
    {
        var table = Assert.Single(Analyze("SELECT | FROM [dbo].[Lib Reader] AS [u x]").Tables);

        Assert.Equal("dbo", table.SchemaName);
        Assert.Equal("Lib Reader", table.ObjectName);
        Assert.Equal("u x", table.Alias);
    }

    [Fact]
    public void 資料表值函式的別名可辨識()
    {
        var table = Assert.Single(Analyze("SELECT | FROM dbo.fn_Split('a,b') s").Tables);

        Assert.Equal("fn_Split", table.ObjectName);
        Assert.Equal("s", table.Alias);
    }

    /// <summary>
    /// 引數清單整段跳過，中間的逗號與巢狀括號都不改變答案。
    /// </summary>
    /// <remarks>
    /// <c>FROM</c> 後面接得了逗號分隔的清單，所以引數的逗號認錯的話會多出一個
    /// 憑空冒出來的來源；而巢狀括號只跳一層的話，別名會被算進資料來源裡。
    /// 兩種都是同一個症狀：<c>f.</c> 一個欄位都列不出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0, N'x') f")]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(dbo.fn_DueDate(GETDATE(), 14)) f")]
    public void 資料表值函式的引數不影響來源清單(string sqlWithCaret)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.Equal("dbo", table.SchemaName);
        Assert.Equal("fn_LoansByReader", table.ObjectName);
        Assert.Equal("f", table.Alias);
        Assert.False(table.IsDerived);
    }

    [Fact]
    public void 衍生資料表標記為無中繼資料但保留別名()
    {
        var table = Assert.Single(Analyze("SELECT | FROM (SELECT 1 AS X) d").Tables);

        Assert.True(table.IsDerived);
        Assert.Equal("d", table.Alias);
    }

    /// <summary>
    /// 別名後面明確寫出的資料行清單要讀出來。
    /// </summary>
    /// <remarks>
    /// 資料表值建構式的欄位名稱<b>只</b>寫在這裡：<c>VALUES</c> 不是 <c>SELECT</c>，
    /// 主體一個名稱都讀不出來。少了這一份的症狀是 <c>T.</c> 一個欄位都列不出來，
    /// <c>SELECT T.*</c> 也展不開。
    /// </remarks>
    [Theory]
    [InlineData("SELECT | FROM (VALUES (1, N'Alice')) AS T (CopyNo, ReaderId)")]
    [InlineData("SELECT | FROM (VALUES (1, N'Alice')) T (CopyNo, ReaderId)")]
    [InlineData("SELECT | FROM (SELECT CopyNo, ReaderId FROM dbo.Loan) AS T (CopyNo, ReaderId)")]
    public void 讀出別名後面的資料行清單(string sqlWithCaret)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.Equal("T", table.Alias);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, table.ColumnNames);
    }

    /// <summary>
    /// 資料列集函式與衍生資料表一樣接得住資料行清單。
    /// </summary>
    /// <remarks>
    /// 文法上的 <c>rowset_function</c>，與使用者定義的資料表值函式同形狀卻不同待遇，
    /// 所以那三個名字只能寫死。
    /// </remarks>
    [Theory]
    [InlineData("SELECT | FROM OPENQUERY(LibArchive, 'SELECT 1, 2') AS T (CopyNo, ReaderId)")]
    [InlineData("SELECT | FROM OPENROWSET(BULK N'loans.csv', SINGLE_CLOB) T (CopyNo, ReaderId)")]
    public void 資料列集函式的別名後面讀得到資料行清單(string sqlWithCaret)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.Equal("T", table.Alias);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, table.ColumnNames);
    }

    /// <summary>
    /// 其餘具名來源後面那串括號是資料表提示，不是資料行清單。
    /// </summary>
    /// <remarks>
    /// T-SQL 的 <c>table_source</c> 文法裡只有 <c>derived_table</c> 與
    /// <c>rowset_function</c> 後面有 <c>(column_alias …)</c>；具名資料表與使用者定義
    /// 的資料表值函式後面就只有別名，那串括號是舊式提示。兩者形狀一模一樣，所以憑據
    /// 只能是來源的形狀——實測回報過的症狀是
    /// <c>SELECT * INTO #Temp FROM dbo.fn(x) f (NOLOCK)</c> 之後，<c>#Temp</c> 的結構
    /// 只剩一個叫 NOLOCK 的欄位。
    /// </remarks>
    [Theory]
    [InlineData("SELECT | FROM dbo.Loan l (NOLOCK)", "Loan")]
    [InlineData("SELECT | FROM dbo.Loan WITH (NOLOCK) l", "Loan")]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0) l (NOLOCK)", "fn_LoansByReader")]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0) l (CopyNo, ReaderId)", "fn_LoansByReader")]
    [InlineData("SELECT | FROM dbo.OPENQUERY(0) l (CopyNo, ReaderId)", "OPENQUERY")]
    [InlineData("SELECT | FROM [OPENQUERY](0) l (CopyNo, ReaderId)", "OPENQUERY")]
    public void 具名來源後面的括號不是資料行清單(string sqlWithCaret, string objectName)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.Equal(objectName, table.ObjectName);
        Assert.Equal("l", table.Alias);
        Assert.Empty(table.ColumnNames);
    }

    /// <summary>
    /// 來源後面的資料表提示要整段跳完，否則逗號清單在那裡斷掉。
    /// </summary>
    /// <remarks>
    /// 不讀它的內容與不<b>跳過</b>它是兩件事。只做前者的話，剖析停在括號前面，
    /// 後面那個逗號就不再是來源清單的逗號——症狀是 <c>c.</c> 一個欄位都列不出來，
    /// 而 <c>SELECT *</c> 更糟：它以為只有一個來源，展開成一份少了一半欄位、
    /// 卻仍然執行得動的選取清單。
    ///
    /// 別名之前與之後都要跳：文法把 <c>TABLESAMPLE</c> 與 <c>WITH (…)</c> 排在別名
    /// 之後，而實際指令碼裡兩種順序都寫得出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT | FROM dbo.Loan l (NOLOCK), dbo.Copy c")]
    [InlineData("SELECT | FROM dbo.Loan l WITH (NOLOCK), dbo.Copy c")]
    [InlineData("SELECT | FROM dbo.Loan WITH (NOLOCK) l, dbo.Copy c")]
    [InlineData("SELECT | FROM dbo.Loan l TABLESAMPLE (10 PERCENT), dbo.Copy c")]
    [InlineData("SELECT | FROM dbo.Loan TABLESAMPLE SYSTEM (10 PERCENT) REPEATABLE (205), dbo.Copy c")]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0) l (NOLOCK), dbo.Copy c")]
    [InlineData("SELECT | FROM (VALUES (1)) T (CopyNo), dbo.Copy c")]
    public void 資料表提示不會截斷來源清單(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.Equal(2, scope.Tables.Count);
        Assert.True(scope.TryResolve("c", out var copy));
        Assert.Equal("Copy", copy.ObjectName);
    }

    /// <summary>括號還沒關上時當成沒寫，位置也留在原地。</summary>
    /// <remarks>使用者正打到一半，而讀一半的清單會覆寫掉主體算得出來的名稱。</remarks>
    [Fact]
    public void 還沒關上的資料行清單當成沒寫()
    {
        var table = Assert.Single(Analyze("SELECT | FROM (SELECT CopyNo FROM dbo.Loan) AS T (Cop").Tables);

        Assert.Equal("T", table.Alias);
        Assert.Empty(table.ColumnNames);
    }

    [Fact]
    public void 資料表變數標記為無中繼資料()
    {
        var table = Assert.Single(Analyze("SELECT | FROM @rows r").Tables);

        Assert.True(table.IsDerived);
        Assert.Equal("@rows", table.ObjectName);
        Assert.Equal("r", table.Alias);
    }

    /// <summary>子查詢內的游標看到的是子查詢自己的 FROM，不是外層的。</summary>
    [Fact]
    public void 子查詢內只看得到子查詢的資料來源()
    {
        var scope = Analyze("SELECT * FROM Parent p WHERE Id IN (SELECT | FROM Child c)");

        var table = Assert.Single(scope.Tables);
        Assert.Equal("Child", table.ObjectName);
        Assert.Equal("c", table.Alias);
    }

    /// <summary>反過來，外層的游標不應該看到子查詢裡的資料表。</summary>
    [Fact]
    public void 外層看不到子查詢的資料來源()
    {
        var scope = Analyze("SELECT * FROM (SELECT X FROM Child) d WHERE |");

        var table = Assert.Single(scope.Tables);
        Assert.True(table.IsDerived);
        Assert.Equal("d", table.Alias);
    }

    [Fact]
    public void 只取游標所在的敘述()
    {
        var scope = Analyze("SELECT * FROM Alpha a;\r\nSELECT | FROM Beta b");

        var table = Assert.Single(scope.Tables);
        Assert.Equal("Beta", table.ObjectName);
    }

    [Fact]
    public void GO會切開批次()
    {
        var scope = Analyze("SELECT * FROM Alpha a\r\nGO\r\nSELECT | FROM Beta b");

        Assert.Equal("Beta", Assert.Single(scope.Tables).ObjectName);
    }

    [Fact]
    public void UPDATE的FROM子句仍可解析()
    {
        var scope = Analyze("UPDATE u SET u.Name = 'x' FROM dbo.Lib_Reader u WHERE |");

        Assert.Contains(scope.Tables, t => t.ObjectName == "Lib_Reader" && t.Alias == "u");
    }

    /// <summary>
    /// <c>UPDATE a … FROM T a</c> 的 <c>a</c> 是別名，不是另一個資料來源。
    /// </summary>
    /// <remarks>
    /// 多收一個叫 a 的來源時，中繼資料層會為一個不存在的名稱查一輪，而未限定
    /// 欄位的判斷會因為「有一個來源解析不出來」整段放棄——症狀是 <c>SET |</c> 的
    /// 欄位停上去沒有任何提示，而 <c>a.</c> 的欄位清單卻正常。
    /// </remarks>
    [Theory]
    [InlineData("UPDATE a SET Name = 'x' FROM dbo.Lib_Reader a WHERE |")]
    [InlineData("DELETE FROM a FROM dbo.Lib_Reader a WHERE |")]
    public void 指向別名的更新目標不算資料來源(string sql)
    {
        var scope = Analyze(sql);

        var table = Assert.Single(scope.Tables);
        Assert.Equal("Lib_Reader", table.ObjectName);
        Assert.Equal("a", table.Alias);
    }

    /// <summary>沒有同名別名時，更新目標仍然是一張資料表。</summary>
    [Theory]
    [InlineData("UPDATE Lib_Reader SET Name = 'x' WHERE |")]
    [InlineData("UPDATE dbo.a SET Name = 'x' FROM dbo.Lib_Reader a WHERE |")]
    public void 沒有同名別名的更新目標仍算資料來源(string sql)
    {
        var scope = Analyze(sql);

        Assert.Contains(scope.Tables, t => t.ObjectName is "Lib_Reader" or "a" && t.Alias is null);
    }

    [Fact]
    public void DELETE的FROM子句仍可解析()
    {
        var scope = Analyze("DELETE FROM dbo.Lib_Reader WHERE |");

        Assert.Equal("Lib_Reader", Assert.Single(scope.Tables).ObjectName);
    }

    [Fact]
    public void 註解裡的FROM不算資料來源()
    {
        var scope = Analyze("SELECT * FROM Real r -- FROM Fake f\r\nWHERE |");

        Assert.Equal("Real", Assert.Single(scope.Tables).ObjectName);
    }

    [Fact]
    public void 字串裡的FROM不算資料來源()
    {
        var scope = Analyze("SELECT 'FROM Fake f' FROM Real r WHERE |");

        Assert.Equal("Real", Assert.Single(scope.Tables).ObjectName);
    }

    /// <summary>
    /// INNER 之類的保留字不加方括號就不能當資料表名稱，T-SQL 本身就是這樣規定，
    /// 因此把它當成子句關鍵字而非資料來源才是正確的。
    /// </summary>
    [Fact]
    public void 保留字要加方括號才算資料表名稱()
    {
        Assert.Empty(Analyze("SELECT * FROM Inner i WHERE |").Tables);

        var table = Assert.Single(Analyze("SELECT * FROM [Inner] i WHERE |").Tables);
        Assert.Equal("Inner", table.ObjectName);
        Assert.Equal("i", table.Alias);
    }

    [Fact]
    public void 別名優先於同名資料表()
    {
        var scope = Analyze("SELECT | FROM Loans AS Publisher");

        Assert.True(scope.TryResolve("Publisher", out var reference));
        Assert.Equal("Loans", reference.ObjectName);
    }

    [Fact]
    public void 限定字比對不分大小寫()
    {
        var scope = Analyze("SELECT | FROM dbo.Lib_Reader u");

        Assert.True(scope.TryResolve("U", out var reference));
        Assert.Equal("Lib_Reader", reference.ObjectName);
    }

    [Fact]
    public void 沒有別名時可用資料表名稱解析()
    {
        var scope = Analyze("SELECT | FROM dbo.Lib_Reader");

        Assert.True(scope.TryResolve("Lib_Reader", out var reference));
        Assert.Equal("Lib_Reader", reference.ObjectName);
    }

    [Fact]
    public void 解析不到的限定字回傳false()
    {
        var scope = Analyze("SELECT | FROM dbo.Lib_Reader u");

        Assert.False(scope.TryResolve("zzz", out _));
        Assert.False(scope.TryResolve(string.Empty, out _));
    }

    /// <summary>編輯到一半的敘述是常態，不能丟例外也不能回傳垃圾。</summary>
    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("SELECT |")]
    [InlineData("|")]
    [InlineData("SELECT * FROM dbo.|")]
    [InlineData("SELECT * FROM ( |")]
    public void 不完整的敘述不會丟例外(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.NotNull(scope.Tables);
    }

    [Fact]
    public void FROM之後還沒輸入名稱時沒有資料來源()
    {
        Assert.Empty(Analyze("SELECT * FROM |").Tables);
    }

    /// <summary>
    /// 運算式的括號不切開範圍。
    /// </summary>
    /// <remarks>
    /// 括號在 T-SQL 裡絕大多數時候只是運算式的一部分。全部當成子查詢的話，
    /// <c>SELECT COUNT(a.| FROM T a</c> 的範圍就只剩括號裡那一段，
    /// 別名永遠解析不出來——彙總函式裡沒有欄位建議就是這麼來的。
    /// </remarks>
    [Theory]
    [InlineData("SELECT COUNT(|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT SUM(u.Amount), MAX(|) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT ISNULL(|, 0) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT COUNT(DISTINCT |) FROM dbo.Lib_Reader u")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u WHERE (| = 1)")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u WHERE Id IN (|)")]
    [InlineData("SELECT * FROM dbo.Lib_Reader u GROUP BY DATEPART(day, |)")]
    public void 運算式的括號不切開範圍(string sqlWithCaret)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.Equal("Lib_Reader", table.ObjectName);
        Assert.Equal("u", table.Alias);
    }

    /// <summary>巢狀的函式呼叫一樣不切開。</summary>
    [Fact]
    public void 巢狀函式呼叫不切開範圍()
    {
        var scope = Analyze("SELECT ISNULL(SUM(CONVERT(int, |)), 0) FROM dbo.Lib_Reader u");

        Assert.True(scope.TryResolve("u", out var table));
        Assert.Equal("Lib_Reader", table.ObjectName);
    }

    /// <summary>
    /// 反過來，跟著 SELECT 的括號仍然是子查詢。
    /// </summary>
    /// <remarks>
    /// 這是整條規則的另一半：分不出兩者的話，修好彙總函式就會弄壞子查詢。
    /// </remarks>
    [Fact]
    public void 括號後面接SELECT時仍是子查詢()
    {
        var table = Assert.Single(
            Analyze("SELECT * FROM Parent p WHERE Id IN (SELECT | FROM Child c)").Tables);

        Assert.Equal("Child", table.ObjectName);
    }

    /// <summary>函式的引數裡包著子查詢時，子查詢仍然自成範圍。</summary>
    [Fact]
    public void 函式引數裡的子查詢仍自成範圍()
    {
        var table = Assert.Single(
            Analyze("SELECT ISNULL((SELECT TOP 1 | FROM Child c), 0) FROM Parent p").Tables);

        Assert.Equal("Child", table.ObjectName);
        Assert.Equal("c", table.Alias);
    }

    /// <summary>
    /// INSERT 的資料行清單看得到目標資料表。
    /// </summary>
    /// <remarks>
    /// 順帶的好處：那個括號同樣不是子查詢，因此 <c>INTO t (</c> 裡面
    /// 列得出 <c>t</c> 的欄位——那正是使用者在那個位置要的東西。
    /// </remarks>
    [Fact]
    public void INSERT的資料行清單看得到目標資料表()
    {
        var table = Assert.Single(Analyze("INSERT INTO dbo.Lib_Reader (|)").Tables);

        Assert.Equal("Lib_Reader", table.ObjectName);
    }
}
