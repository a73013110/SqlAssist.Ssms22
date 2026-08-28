using System.Linq;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

public sealed class SqlScopeAnalyzerTests
{
    /// <summary>用 | 標出游標位置，讓測試讀起來就是使用者看到的畫面。</summary>
    private static SqlStatementScope Analyze(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "測試輸入必須用 | 標出游標位置。");
        return SqlScopeAnalyzer.Analyze(sqlWithCaret.Remove(caret, 1), caret);
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
            "SELECT | FROM dbo.Orders o INNER JOIN dbo.Publisher c ON o.PublisherId = c.Id");

        Assert.Equal(new[] { "Orders", "Publisher" }, scope.Tables.Select(t => t.ObjectName));
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
        var table = Assert.Single(Analyze("SELECT * FROM Orders WITH (NOLOCK) o WHERE |").Tables);

        Assert.Equal("Orders", table.ObjectName);
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

    [Fact]
    public void 衍生資料表標記為無中繼資料但保留別名()
    {
        var table = Assert.Single(Analyze("SELECT | FROM (SELECT 1 AS X) d").Tables);

        Assert.True(table.IsDerived);
        Assert.Equal("d", table.Alias);
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
        var scope = Analyze("SELECT | FROM Orders AS Publisher");

        Assert.True(scope.TryResolve("Publisher", out var reference));
        Assert.Equal("Orders", reference.ObjectName);
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
