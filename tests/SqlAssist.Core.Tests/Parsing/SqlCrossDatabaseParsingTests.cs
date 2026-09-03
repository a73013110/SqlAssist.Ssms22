using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// 三段式與四段式名稱在解析層要完整保留。
/// </summary>
/// <remarks>
/// 這一份守的是同一個症狀：段數被截掉之後，下游會拿目前連線裡同名的那一個物件
/// 來回答，而使用者完全看不出來。四條路徑（敘述裡的資料來源、滑鼠停留處的識別字、
/// <c>EXEC</c> 呼叫的模組、限定字）各自截過一次，所以四條都要有測試。
/// </remarks>
public sealed class SqlCrossDatabaseParsingTests
{
    private static SqlStatementScope Analyze(string sqlWithCaret)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        return SqlScopeAnalyzer.Analyze(input.Text, input.Caret);
    }

    private static SqlIdentifierReference? FindAtMarker(string textWithMarker)
    {
        var input = SqlWithCaret.Parse(textWithMarker);
        return SqlIdentifierScanner.FindAt(input.Text, input.Caret);
    }

    private static SqlExecutedModule? FindModule(string sql)
    {
        return SqlExecutedModule.Find(SqlTokenizer.Tokenize(sql));
    }

    [Fact]
    public void 資料來源保留資料庫段()
    {
        var scope = Analyze("SELECT | FROM LibArchive.dbo.Loan AS l");

        Assert.True(scope.TryResolve("l", out var table));
        Assert.Equal("LibArchive", table.DatabaseName);
        Assert.Equal("dbo", table.SchemaName);
        Assert.Equal("Loan", table.ObjectName);
        Assert.Null(table.ServerName);
        Assert.False(table.IsLocal);
    }

    /// <remarks>
    /// 連結伺服器可以直接以位址命名，那時它只有加了方括號才寫得出來。
    /// 位址裡的點號不能被當成分段——被當成分段的話這個名稱會變成七段。
    /// </remarks>
    [Fact]
    public void 資料來源保留以位址命名的連結伺服器段()
    {
        var scope = Analyze("SELECT TOP 100 | FROM [192.0.2.10].[LibArchive].[dbo].[Loan] AS l");

        Assert.True(scope.TryResolve("l", out var table));
        Assert.Equal("192.0.2.10", table.ServerName);
        Assert.Equal("LibArchive", table.DatabaseName);
        Assert.Equal("dbo", table.SchemaName);
        Assert.Equal("Loan", table.ObjectName);
        Assert.False(table.IsLocal);
    }

    /// <remarks>
    /// 沒有別名時，用來限定欄位的是名稱本體而不是整串四段式名稱。
    /// </remarks>
    [Fact]
    public void 沒有別名時以名稱本體限定欄位()
    {
        var scope = Analyze("SELECT | FROM LibArchive.dbo.Loan");

        Assert.True(scope.TryResolve("Loan", out var table));
        Assert.Equal("LibArchive", table.DatabaseName);
    }

    /// <remarks>
    /// <c>db..object</c> 少寫的是結構描述，不是資料庫。補空段而不是跳過，
    /// 右對齊時位置才對得回去。
    /// </remarks>
    [Fact]
    public void 省略結構描述的三段式仍讀得出資料庫()
    {
        var scope = Analyze("SELECT | FROM LibArchive..Loan AS l");

        Assert.True(scope.TryResolve("l", out var table));
        Assert.Equal("LibArchive", table.DatabaseName);
        Assert.Null(table.SchemaName);
        Assert.Equal("Loan", table.ObjectName);
    }

    /// <remarks>
    /// 段數過多的名稱查不到，但別名仍要記下來——丟掉的話後面用這個別名限定欄位
    /// 會被誤判成結構描述，於是清單改列一個叫做那個別名的結構描述底下的物件。
    /// </remarks>
    [Fact]
    public void 段數過多時仍記得別名但查不到()
    {
        var scope = Analyze("SELECT | FROM a.b.c.d.e AS l");

        Assert.True(scope.TryResolve("l", out var table));
        Assert.True(table.IsDerived);
        Assert.Null(table.Path);
    }

    [Fact]
    public void 本地名稱仍然是本地()
    {
        var scope = Analyze("SELECT | FROM dbo.Loan AS l");

        Assert.True(scope.TryResolve("l", out var table));
        Assert.True(table.IsLocal);
        Assert.Null(table.DatabaseName);
        Assert.Equal("dbo", table.SchemaName);
    }

    [Fact]
    public void 滑鼠停留讀得出四段式名稱()
    {
        var reference = FindAtMarker("SELECT * FROM [192.0.2.10].[LibArchive].[dbo].[Lo|an]");

        Assert.NotNull(reference);
        Assert.Equal("Loan", reference!.Name);
        Assert.NotNull(reference.Path);
        Assert.Equal("192.0.2.10", reference.Path!.ServerName);
        Assert.Equal("LibArchive", reference.Path.DatabaseName);
        Assert.Equal("dbo", reference.Path.SchemaName);
        Assert.False(reference.IsLocal);
    }

    /// <remarks>
    /// 整個參考的範圍要含前面每一段，否則 F12 反白與取代的長度會少掉限定字。
    /// </remarks>
    [Fact]
    public void 滑鼠停留的範圍涵蓋整串名稱()
    {
        const string sql = "SELECT * FROM LibArchive.dbo.Loan";
        var reference = SqlIdentifierScanner.FindAt(sql, sql.Length - 2);

        Assert.NotNull(reference);
        Assert.Equal(sql.IndexOf("LibArchive", System.StringComparison.Ordinal), reference!.Start);
        Assert.Equal("LibArchive.dbo.Loan", sql.Substring(reference.Start, reference.Length));
    }

    [Fact]
    public void 滑鼠停留讀得出省略結構描述的三段式()
    {
        var reference = FindAtMarker("SELECT * FROM LibArchive..Lo|an");

        Assert.NotNull(reference);
        Assert.NotNull(reference!.Path);
        Assert.Equal("LibArchive", reference.Path!.DatabaseName);
        Assert.Null(reference.Path.SchemaName);
    }

    /// <remarks>
    /// 段數過多時不留下路徑，讓下游明確地查不到，而不是拿最後四段去猜。
    /// </remarks>
    [Fact]
    public void 滑鼠停留在段數過多的名稱上不猜()
    {
        var reference = FindAtMarker("SELECT * FROM a.b.c.d.|e");

        Assert.NotNull(reference);
        Assert.Equal("e", reference!.Name);
        Assert.Null(reference.Path);
        Assert.False(reference.IsLocal);
    }

    [Fact]
    public void 兩段式的滑鼠停留不受影響()
    {
        var reference = FindAtMarker("SELECT * FROM dbo.Lo|an");

        Assert.NotNull(reference);
        Assert.Equal("dbo", reference!.Qualifier);
        Assert.True(reference.IsLocal);
    }

    /// <remarks>
    /// 曾經只取後兩段，理由是「至少讓同名的那一個對得上」。對得上的是另一個程序，
    /// 而參數清單長得不一樣時，使用者按著提示填完的每一個引數都是錯的。
    /// </remarks>
    [Fact]
    public void EXEC保留跨資料庫的程序位置()
    {
        var module = FindModule("EXEC LibArchive.dbo.usp_Renew @");

        Assert.NotNull(module);
        Assert.Equal("LibArchive", module!.Path.DatabaseName);
        Assert.Equal("dbo", module.SchemaName);
        Assert.Equal("usp_Renew", module.ObjectName);
        Assert.False(module.IsLocal);
    }

    [Fact]
    public void EXEC的本地程序仍然是本地()
    {
        var module = FindModule("EXEC dbo.usp_Renew @");

        Assert.NotNull(module);
        Assert.True(module!.IsLocal);
        Assert.Equal("dbo", module.SchemaName);
    }

    [Fact]
    public void EXEC保留四段式的程序位置()
    {
        var module = FindModule("EXEC [192.0.2.10].[LibArchive].[dbo].[usp_Renew] @");

        Assert.NotNull(module);
        Assert.Equal("192.0.2.10", module!.Path.ServerName);
        Assert.False(module.IsLocal);
    }

    /// <remarks>
    /// 段數過多讀不出位置就整個不認：認一半的話下游會拿最後四段去查一個
    /// 使用者沒有指名的程序。
    /// </remarks>
    [Fact]
    public void EXEC段數過多時不認()
    {
        Assert.Null(FindModule("EXEC a.b.c.d.e @"));
    }

    /// <remarks>
    /// 每一條路徑都要走同一個右對齊規則，這裡直接比對三條路徑算出來的位置。
    /// 分岔的症狀是同一個名稱在建議清單裡認得、在 F12 那條路上不認得。
    /// </remarks>
    [Fact]
    public void 三條路徑對同一個名稱算出同一個位置()
    {
        var scope = Analyze("SELECT | FROM LibArchive.dbo.Loan AS l");
        Assert.True(scope.TryResolve("l", out var table));

        var reference = FindAtMarker("SELECT * FROM LibArchive.dbo.Lo|an");
        var paths = new List<SqlObjectPath?> { table.Path, reference?.Path };

        foreach (var path in paths)
        {
            Assert.NotNull(path);
            Assert.Equal("LibArchive", path!.DatabaseName);
            Assert.Equal("dbo", path.SchemaName);
            Assert.Equal("Loan", path.Name);
        }
    }
}
