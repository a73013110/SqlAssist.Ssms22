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

    /// <summary>
    /// 括起一段聯結的括號不是衍生資料表：裡面的來源與別名屬於外面那一層。
    /// </summary>
    [Theory]
    [InlineData("SELECT * FROM (Lib_Reader a JOIN Loan AS b ON |a.x = b.x)", "a,b")]
    [InlineData("SELECT * FROM (Lib_Reader a JOIN Loan AS b ON |", "a,b")]
    [InlineData("SELECT * FROM Branch r JOIN (Lib_Reader a JOIN Loan b ON |a.x = b.x) ON r.x = a.x", "r,a,b")]
    [InlineData("SELECT * FROM ((Lib_Reader a JOIN Loan b ON a.x = b.x)) WHERE |", "a,b")]
    [InlineData("MERGE Loan USING ((SELECT @a, @b) AS s (c1, c2) JOIN Copy ON |s.c1 = Copy.CopyNo) ON 1 = 1", "Loan,s,Copy")]
    public void 括號聯結的來源屬於外層(string sqlWithCaret, string qualifiers)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.Equal(qualifiers.Split(','), scope.Tables.Select(table => table.Alias ?? table.ObjectName));
    }

    [Fact]
    public void 括號聯結裡的衍生資料表仍讀得到資料行清單()
    {
        var scope = Analyze("MERGE Loan USING ((SELECT @a, @b) AS s (c1, c2) JOIN Copy ON s.| = Copy.CopyNo) ON 1 = 1");

        Assert.True(scope.TryResolve("s", out var source));
        Assert.True(source.IsDerived);
        Assert.Equal(new[] { "c1", "c2" }, source.ColumnNames);
    }

    /// <summary>整個查詢包在兩層括號裡、或括號之後接集合運算子時，外層仍是衍生資料表。</summary>
    [Theory]
    [InlineData("SELECT | FROM ((SELECT 1 AS X)) d")]
    [InlineData("SELECT | FROM ((SELECT 1 AS X) UNION (SELECT 2)) d")]
    public void 包住整個查詢的括號仍是衍生資料表(string sqlWithCaret)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.True(table.IsDerived);
        Assert.Equal("d", table.Alias);
    }

    /// <summary>MERGE 的目標不寫 INTO 時一樣是資料來源，別名解析得到。</summary>
    [Theory]
    [InlineData("MERGE dbo.Loan t USING dbo.Copy s ON |")]
    [InlineData("MERGE dbo.Loan AS t USING dbo.Copy AS s ON t.CopyNo = s.CopyNo WHEN MATCHED THEN UPDATE SET CopyNo = t.|")]
    public void MERGE不寫INTO的目標也是資料來源(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.Equal(2, scope.Tables.Count);
        Assert.True(scope.TryResolve("t", out var target));
        Assert.Equal("Loan", target.ObjectName);
    }

    /// <summary>寫完的 CASE … END 是運算元：FROM 仍屬於 SELECT，後面的別名照收。</summary>
    [Theory]
    [InlineData("SELECT CASE WHEN 1 = 1 THEN 1 ELSE 2 END FROM dbo.Loan a LEFT JOIN dbo.Copy b ON |")]
    [InlineData("SELECT CASE CopyNo WHEN 1 THEN 'x' END AS c FROM dbo.Loan a LEFT JOIN dbo.Copy b ON |")]
    public void CASE之後的FROM仍收資料來源(string sqlWithCaret)
    {
        Assert.Equal(new[] { "a", "b" }, Analyze(sqlWithCaret).Tables.Select(table => table.Alias));
    }

    /// <summary>時態表的 FOR SYSTEM_TIME 排在別名前面，跳過之後別名照讀。</summary>
    [Theory]
    [InlineData("SELECT | FROM dbo.Loan FOR SYSTEM_TIME AS OF @d AS a JOIN dbo.Copy b ON a.CopyNo = b.CopyNo")]
    [InlineData("SELECT | FROM dbo.Loan FOR SYSTEM_TIME FROM '2020-01-01' TO SYSDATETIME() a JOIN dbo.Copy b ON a.CopyNo = b.CopyNo")]
    [InlineData("SELECT | FROM dbo.Loan FOR SYSTEM_TIME BETWEEN @s AND @e a JOIN dbo.Copy b ON a.CopyNo = b.CopyNo")]
    [InlineData("SELECT | FROM dbo.Loan FOR SYSTEM_TIME CONTAINED IN (@s, @e) a JOIN dbo.Copy b ON a.CopyNo = b.CopyNo")]
    [InlineData("SELECT | FROM dbo.Loan FOR SYSTEM_TIME ALL a JOIN dbo.Copy b ON a.CopyNo = b.CopyNo")]
    public void 時態子句之後讀得到別名(string sqlWithCaret)
    {
        Assert.Equal(new[] { "a", "b" }, Analyze(sqlWithCaret).Tables.Select(table => table.Alias));
    }

    [Fact]
    public void 聯結提示的MERGE不收成來源()
    {
        var scope = Analyze("SELECT | FROM dbo.Loan l INNER MERGE JOIN dbo.Copy c ON l.CopyNo = c.CopyNo");

        Assert.Equal(new[] { "Loan", "Copy" }, scope.Tables.Select(table => table.ObjectName));
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
    /// 文法上的 <c>rowset_function</c> 只接資料行清單，所以那三個名字只能寫死。
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
    /// 具名資料表後面那串括號是資料表提示；使用者定義的函式後面每一項都是提示時才是提示。
    /// </summary>
    /// <remarks>
    /// 函式後面兩種都寫得出來，形狀一模一樣。整份當清單的症狀是
    /// <c>SELECT * INTO #Temp FROM dbo.fn(x) f (NOLOCK)</c> 之後，<c>#Temp</c> 的結構
    /// 只剩一個叫 NOLOCK 的欄位。
    /// </remarks>
    [Theory]
    [InlineData("SELECT | FROM dbo.Loan l (NOLOCK)", "Loan")]
    [InlineData("SELECT | FROM dbo.Loan l (CopyNo)", "Loan")]
    [InlineData("SELECT | FROM dbo.Loan WITH (NOLOCK) l", "Loan")]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0) l (NOLOCK)", "fn_LoansByReader")]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0) l (NOLOCK, INDEX (1))", "fn_LoansByReader")]
    public void 資料表提示不是資料行清單(string sqlWithCaret, string objectName)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.Equal(objectName, table.ObjectName);
        Assert.Equal("l", table.Alias);
        Assert.Empty(table.ColumnNames);
    }

    /// <summary>使用者定義的函式後面寫的不是提示，就是資料行清單。</summary>
    /// <remarks>整份當提示的症狀是 <c>CROSS APPLY l.</c> 列不出 <c>CopyNo</c>。</remarks>
    [Theory]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0) AS l (CopyNo, ReaderId)", "fn_LoansByReader")]
    [InlineData("SELECT | FROM dbo.fn_LoansByReader(0) l (CopyNo, ReaderId)", "fn_LoansByReader")]
    [InlineData("SELECT | FROM dbo.OPENQUERY(0) l (CopyNo, ReaderId)", "OPENQUERY")]
    [InlineData("SELECT | FROM [OPENQUERY](0) l (CopyNo, ReaderId)", "OPENQUERY")]
    public void 使用者定義的函式後面讀得到資料行清單(string sqlWithCaret, string objectName)
    {
        var table = Assert.Single(Analyze(sqlWithCaret).Tables);

        Assert.Equal(objectName, table.ObjectName);
        Assert.Equal("l", table.Alias);
        Assert.Equal(new[] { "CopyNo", "ReaderId" }, table.ColumnNames);
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

    /// <summary>
    /// 相互關聯子查詢引用的外層別名解析得出來；未限定的來源仍只有子查詢自己那一層。
    /// </summary>
    /// <remarks>
    /// 只看子查詢那一層的話，<c>a.</c> 退回「a 是結構描述」的解讀，一個欄位都列不出來。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan a JOIN dbo.Copy b ON b.CopyNo = a.CopyNo\nWHERE NOT EXISTS(SELECT * FROM dbo.Branch c WHERE c.CopyNo = a.|)")]
    [InlineData("SELECT * FROM dbo.Loan a JOIN dbo.Copy b ON b.CopyNo = a.CopyNo\nWHERE NOT EXISTS(SELECT * FROM dbo.Branch c WHERE c.CopyNo = a.|CopyNo)")]
    [InlineData("SELECT * FROM dbo.Loan a WHERE a.CopyNo IN (SELECT c.CopyNo FROM dbo.Branch c WHERE EXISTS (SELECT 1 FROM dbo.Copy d WHERE d.CopyNo = a.|))")]
    public void 相互關聯子查詢看得到外層的別名(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.DoesNotContain(scope.Tables, table => table.Alias == "a");
        Assert.True(scope.TryResolve("a", out var outer));
        Assert.Equal("Loan", outer.ObjectName);
    }

    /// <summary>子查詢裡與外層同名的別名遮住外層那一個。</summary>
    [Fact]
    public void 內層別名遮住外層同名的別名()
    {
        var scope = Analyze("SELECT * FROM dbo.Loan a WHERE EXISTS (SELECT 1 FROM dbo.Copy a WHERE a.|)");

        Assert.True(scope.TryResolve("a", out var table));
        Assert.Equal("Copy", table.ObjectName);
        Assert.Equal("Loan", Assert.Single(scope.Outer!.Tables).ObjectName);
    }

    /// <summary>不在子查詢裡的範圍沒有外層；分號之後的下一句不是外層。</summary>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan a WHERE a.|")]
    [InlineData("SELECT * FROM dbo.Copy b;\nSELECT * FROM dbo.Loan a WHERE a.|")]
    [InlineData("SELECT COUNT(a.|) FROM dbo.Loan a")]
    public void 子查詢以外沒有外層(string sqlWithCaret)
    {
        Assert.Null(Analyze(sqlWithCaret).Outer);
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

    /// <summary>
    /// 範圍的界線與位置分析同一條判準：名單外的語句（BACKUP、RESTORE、THROW、BEGIN）也是界線。
    /// </summary>
    /// <remarks>
    /// 游標後面那一句要切得開：<c>ON source.|</c> 這一格還沒寫完，而下一行的 RESTORE 不能被當成
    /// <c>source.RESTORE</c>，它的 <c>DISK</c> 也不能被收成一個資料來源。
    /// </remarks>
    [Theory]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.|\nRESTORE DATABASE LibArchive FROM DISK = 'x'")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = |\nRESTORE DATABASE LibArchive FROM DISK = 'x'")]
    [InlineData("SELECT * FROM dbo.Loan AS target JOIN dbo.LoanDetail AS source ON source.|\nBACKUP DATABASE LibArchive TO DISK = 'x'")]
    [InlineData("SELECT * FROM dbo.Loan AS target JOIN dbo.LoanDetail AS source ON source.CopyNo = target.CopyNo WHERE |\nUPDATE dbo.Branch SET x = 1")]
    [InlineData("SELECT * FROM dbo.Loan AS target, dbo.LoanDetail AS source WHERE |\nBEGIN TRAN\nDELETE dbo.Branch")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.LoanDetail AS source\n    ON target.CopyNo = source.|\nDELETE FROM dbo.Branch")]
    [InlineData("SELECT * FROM dbo.Loan AS target JOIN dbo.LoanDetail AS source ON source.|\nUPDATE dbo.Branch SET x = 1")]
    public void 游標所在的敘述不會延伸進下一句(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.Equal(new[] { "Loan", "LoanDetail" }, scope.Tables.Select(table => table.ObjectName));
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Loan WHERE CopyNo = 1\nRESTORE DATABASE LibArchive FROM DISK = 'x' WITH |")]
    [InlineData("SELECT * FROM dbo.Loan WHERE CopyNo = 1\nBACKUP DATABASE LibArchive TO DISK = 'x' WITH |")]
    [InlineData("SELECT * FROM dbo.Loan WHERE CopyNo = 1\nTHROW 50000, N'x', |")]
    [InlineData("SELECT * FROM dbo.Loan WHERE CopyNo = 1\nRAISERROR (N'x', 16, 1) WITH |")]
    [InlineData("SELECT * FROM dbo.Loan WHERE CopyNo = 1\nWAITFOR DELAY |")]
    [InlineData("SELECT * FROM dbo.Loan WHERE CopyNo = 1\nCOMMIT TRAN |")]
    public void 名單外的語句不會接到上一句(string sqlWithCaret)
    {
        Assert.Empty(Analyze(sqlWithCaret).Tables);
    }

    /// <summary>接在隱含界線後面、卻屬於同一句的字不切斷範圍。</summary>
    [Theory]
    [InlineData("UPDATE l\nSET CopyNo = |\nFROM dbo.Loan l\nJOIN dbo.Copy c ON c.CopyNo = l.CopyNo")]
    [InlineData("UPDATE l WITH (ROWLOCK)\nSET CopyNo = |\nFROM dbo.Loan l\nJOIN dbo.Copy c ON c.CopyNo = l.CopyNo")]
    public void UPDATE換行寫的SET仍屬於同一句(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.Equal(new[] { "Loan", "Copy" }, scope.Tables.Select(table => table.ObjectName));
    }

    /// <summary>
    /// FROM 接不接資料來源由它所屬的動詞決定：游標、備份裝置、主體與檔案都不是資料表。
    /// </summary>
    [Theory]
    [InlineData("FETCH NEXT FROM LoanCursor INTO |", "LoanCursor")]
    [InlineData("RESTORE DATABASE LibArchive FROM DISK = 'x' WITH |", "DISK")]
    [InlineData("REVOKE SELECT ON dbo.Loan FROM Lib_Reader |", "Lib_Reader")]
    [InlineData("REVOKE SELECT (CopyNo), INSERT ON dbo.Loan FROM Lib_Reader |", "Lib_Reader")]
    [InlineData("CREATE LOGIN Lib_Reader FROM WINDOWS |", "WINDOWS")]
    public void 不接資料來源的FROM不收名稱(string sqlWithCaret, string name)
    {
        Assert.DoesNotContain(Analyze(sqlWithCaret).Tables, table => table.ObjectName == name);
    }

    [Theory]
    [InlineData("SELECT (CopyNo) AS c FROM dbo.Loan WHERE |")]
    [InlineData("SELECT TOP (5) WITH TIES CopyNo FROM dbo.Loan ORDER BY |")]
    [InlineData("DELETE FROM dbo.Loan WHERE |")]
    [InlineData("UPDATE l SET CopyNo = 1 FROM dbo.Loan l WHERE |")]
    public void 查詢與更新的FROM照舊收資料來源(string sqlWithCaret)
    {
        Assert.Contains(Analyze(sqlWithCaret).Tables, table => table.ObjectName == "Loan");
    }

    /// <summary>APPLY 右邊是為了逐列引用左邊而寫的：沒寫限定字的欄位也屬於左邊的來源，<c>*</c> 仍只屬於這一層。</summary>
    [Fact]
    public void APPLY右邊的衍生資料表看得到左邊的來源()
    {
        var scope = Analyze("SELECT x.Big FROM (VALUES (1)) d (Seq) CROSS APPLY dbo.fn_Loans(1) f CROSS APPLY (SELECT CAST(| AS bigint) AS Big FROM dbo.Copy c) x");

        Assert.Equal(new[] { "c" }, scope.Tables.Select(table => table.EffectiveName));
        Assert.Equal(new[] { "d", "f" }, scope.Lateral.Select(table => table.EffectiveName));
        Assert.Equal(new[] { "c", "d", "f" }, scope.ColumnTables.Select(table => table.EffectiveName));
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Loan l WHERE EXISTS (SELECT | FROM dbo.Copy c)")]
    [InlineData("SELECT * FROM dbo.Loan l JOIN (SELECT | FROM dbo.Copy c) x ON 1 = 1")]
    public void 其餘子查詢沒寫限定字的欄位只屬於這一層(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.Empty(scope.Lateral);
        Assert.Equal(new[] { "c" }, scope.ColumnTables.Select(table => table.EffectiveName));
    }

    /// <summary>PIVOT 轉出來的是新來源，之後以它的別名引用；轉之前的來源照樣留著，PIVOT 括號裡引用的是它。</summary>
    [Theory]
    [InlineData("SELECT | FROM (SELECT Branch, Fee FROM dbo.Loan) s PIVOT (MAX(Fee) FOR Branch IN ([0], [1])) P")]
    [InlineData("SELECT | FROM dbo.Loan s UNPIVOT (Fee FOR Branch IN (Fee0, Fee1)) AS P JOIN dbo.Copy c ON 1 = 1")]
    public void PIVOT的別名是資料來源(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.True(scope.TryResolve("P", out var pivot));
        Assert.True(pivot.IsDerived);
        Assert.True(scope.TryResolve("s", out _));
    }

    /// <summary>資料列集函式的 <c>TABLE = 來源</c> 與函式本身同一層；括號還沒關上時照樣讀得到。</summary>
    [Theory]
    [InlineData("SELECT * FROM VECTOR_SEARCH(TABLE = dbo.Copy AS c, COLUMN = Embedding, SIMILAR_TO = @v, METRIC = 'cosine', TOP_N = 5) AS s ORDER BY |")]
    [InlineData("SELECT * FROM VECTOR_SEARCH(TABLE = dbo.Copy AS c, COLUMN = c.|")]
    public void 具名引數TABLE是資料來源(string sqlWithCaret)
    {
        var scope = Analyze(sqlWithCaret);

        Assert.True(scope.TryResolve("c", out var table));
        Assert.Equal("Copy", table.ObjectName);
    }
}
