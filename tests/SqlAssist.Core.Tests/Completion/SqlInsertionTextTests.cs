using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 提交一筆建議時寫進編輯器的那串字。
/// </summary>
/// <remarks>
/// 這裡的每一條都曾經只能靠人開著 SSMS 打一遍才看得出來：多一個點號、少一段
/// 結構描述、方括號包到不該包的名稱上，三種都不會報錯，只會讓提交完的那一行
/// 執行失敗，或是逼使用者退掉一個他沒要求的字元。
/// </remarks>
public sealed class SqlInsertionTextTests
{
    private static readonly SqlAssistSettings Qualified =
        new() { QualifyObjectNames = true, UseSquareBrackets = false };

    private static readonly SqlAssistSettings Unqualified =
        new() { QualifyObjectNames = false, UseSquareBrackets = false };

    private static readonly SqlAssistSettings Bracketed =
        new() { QualifyObjectNames = true, UseSquareBrackets = true };

    private static SqlSuggestion Table(string name, string? schema = "dbo") =>
        new(name, name, "Table", name, SuggestionKind.Table, schemaName: schema);

    /// <summary>
    /// 照中繼資料認出的結果重新對齊，再算插入文字。
    /// </summary>
    /// <remarks>
    /// 只看文字的話 <c>LibArchive.</c> 一律是結構描述那一格（右對齊是唯一猜得出的
    /// 假設），要挪到資料庫那一格得先問中繼資料。單元測試沒有連線，所以照
    /// <c>SqlQualifierResolver</c> 的結果直接呼叫 <see cref="SqlObjectPath.TryRealign"/>，
    /// 與 <c>SqlLinkedServerCompletionTests</c> 走同一條。
    /// </remarks>
    private static string Build(
        SqlSuggestion suggestion,
        string sqlWithCaret,
        SqlAssistSettings settings,
        SqlQualifierSlot? leftmost = null)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);
        var context = SqlCompletionContextAnalyzer.Analyze(input.Text, input.Caret);

        if (leftmost is { } slot && context.QualifierPath!.TryRealign(slot, out var realigned))
        {
            context = context.WithQualifierPath(realigned);
        }

        return SqlInsertionText.Build(suggestion, context, settings);
    }

    [Fact]
    public void 沒有限定字時補不補結構描述由偏好決定()
    {
        Assert.Equal("dbo.Loan", Build(Table("Loan"), "SELECT * FROM |", Qualified));
        Assert.Equal("Loan", Build(Table("Loan"), "SELECT * FROM |", Unqualified));
    }

    /// <remarks>
    /// 這一條不歸偏好管：<c>LibArchive.Loan</c> 是兩段式，會被讀成「結構描述
    /// LibArchive」，而那個結構描述並不存在。關掉一個為了少打幾個字的偏好，
    /// 不代表要產生執行不了的語法。
    /// </remarks>
    [Fact]
    public void 限定字停在資料庫那一格一定補結構描述()
    {
        Assert.Equal(
            "dbo.Loan",
            Build(Table("Loan"), "SELECT * FROM LibArchive.|", Unqualified, SqlQualifierSlot.Database));
    }

    /// <remarks>
    /// 補了會寫出四段式的 <c>LibArchive..dbo.Loan</c>，而使用者打的第二個點號
    /// 正是在說「照預設解析」。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.|")]
    [InlineData("SELECT * FROM LibArchive.dbo.|")]
    [InlineData("SELECT * FROM LibArchive..|")]
    public void 限定字停在結構描述那一格就不再補(string sqlWithCaret)
    {
        Assert.Equal("Loan", Build(Table("Loan"), sqlWithCaret, Qualified));
    }

    /// <remarks>
    /// 這三類的 SchemaName 就是它們自己，走物件那條會寫出 <c>dbo.dbo</c>；
    /// 而點號留給使用者自己打——提交的意思是「我要這個名稱」，不是
    /// 「我要繼續往下走」。
    /// </remarks>
    [Fact]
    public void 路徑的中間段只寫名稱本身()
    {
        var schema = new SqlSuggestion("dbo", "dbo.", "Schema", "dbo", SuggestionKind.Schema, schemaName: "dbo");
        var database = new SqlSuggestion("LibArchive", "LibArchive", "Database", "LibArchive", SuggestionKind.Database);
        var server = new SqlSuggestion("LibMirror", "LibMirror", "Linked server", "LibMirror", SuggestionKind.LinkedServer);

        Assert.Equal("dbo", Build(schema, "SELECT * FROM |", Qualified));
        Assert.Equal("LibArchive", Build(database, "USE |", Qualified));
        Assert.Equal("LibMirror", Build(server, "SELECT * FROM |", Qualified));
    }

    /// <remarks>
    /// 插入文字在建立建議時就定案的那幾類，這裡原樣送出：欄位帶著別名限定、
    /// 內建函式帶著左括號、參數帶著 <c> = </c>。把 <c>@@ROWCOUNT</c> 當成物件名稱
    /// 去套設定，寫進編輯器的會是 <c>[@@ROWCOUNT]</c>。
    /// </remarks>
    [Theory]
    [InlineData(SuggestionKind.Keyword, "SELECT")]
    [InlineData(SuggestionKind.Snippet, "SELECT * FROM ")]
    [InlineData(SuggestionKind.Column, "lr.ReaderId")]
    [InlineData(SuggestionKind.BuiltInFunction, "COUNT(")]
    [InlineData(SuggestionKind.GlobalVariable, "@@ROWCOUNT")]
    [InlineData(SuggestionKind.Variable, "@readerId")]
    [InlineData(SuggestionKind.DataType, "nvarchar(")]
    [InlineData(SuggestionKind.Parameter, "@readerId = ")]
    [InlineData(SuggestionKind.DatePart, "DAY")]
    [InlineData(SuggestionKind.TableHint, "NOLOCK")]
    [InlineData(SuggestionKind.QueryHint, "RECOMPILE")]
    public void 自己帶插入文字的類別原樣送出(SuggestionKind kind, string insertionText)
    {
        // 用最會改寫的設定：這幾類連結構描述與方括號都不該碰得到。
        var suggestion = new SqlSuggestion("ReaderId", insertionText, "", "", kind, schemaName: "dbo");

        Assert.Equal(insertionText, Build(suggestion, "SELECT * FROM |", Bracketed));
    }

    /// <remarks>
    /// 反過來的一半：資料庫物件的每一種都要走結構描述那條規則。少一種的症狀是
    /// 那一類的名稱單獨少了 <c>dbo.</c>，而清單上看不出它和別人有什麼不同。
    /// </remarks>
    [Theory]
    [InlineData(SuggestionKind.Table)]
    [InlineData(SuggestionKind.View)]
    [InlineData(SuggestionKind.Procedure)]
    [InlineData(SuggestionKind.Function)]
    [InlineData(SuggestionKind.TableFunction)]
    [InlineData(SuggestionKind.Trigger)]
    [InlineData(SuggestionKind.Sequence)]
    [InlineData(SuggestionKind.UserDefinedType)]
    public void 資料庫物件都照結構描述規則補(SuggestionKind kind)
    {
        var suggestion = new SqlSuggestion("Loan", "Loan", "", "", kind, schemaName: "dbo");

        Assert.Equal("dbo.Loan", Build(suggestion, "SELECT * FROM |", Qualified));
    }

    [Fact]
    public void 一律加方括號時結構描述與物件都要包()
    {
        Assert.Equal("[dbo].[Lib_Reader]", Build(Table("Lib_Reader"), "SELECT * FROM |", Bracketed));
    }

    /// <remarks>
    /// 關掉「一律加方括號」只代表不想看到多餘的括號，不是要產生無效語法：
    /// <c>Order</c> 的形狀完全合格，不包起來卻是語法錯誤。
    /// </remarks>
    [Fact]
    public void 關掉方括號時保留字仍要包()
    {
        Assert.Equal("dbo.[Order]", Build(Table("Order"), "SELECT * FROM |", Qualified));
    }

    /// <remarks>
    /// 指令碼自己宣告的名稱不在這個設定的管轄內：<c>[#LoanImport]</c> 合法卻不是
    /// 任何人會手寫的樣子，而它們也沒有結構描述可以補。
    /// </remarks>
    [Fact]
    public void 暫存資料表不受一律加方括號管()
    {
        var staging = new SqlSuggestion(
            "#LoanImport", "#LoanImport", "", "", SuggestionKind.ScriptDataSource);

        Assert.Equal("#LoanImport", Build(staging, "SELECT * FROM |", Bracketed));
    }

    /// <remarks>
    /// <see cref="SqlInsertionText.Quote"/> 是公開的：展開萬用字元與建立欄位建議
    /// 都用它，各自照設定再判斷一次就會分岔。
    /// </remarks>
    [Theory]
    [InlineData("CopyNo", false, "CopyNo")]
    [InlineData("CopyNo", true, "[CopyNo]")]
    [InlineData("User", false, "[User]")]
    [InlineData("Loan Detail", false, "[Loan Detail]")]
    [InlineData("@rows", true, "@rows")]
    [InlineData("#LoanImport", true, "#LoanImport")]
    public void 方括號只在必要或設定要求時出現(string name, bool useSquareBrackets, string expected)
    {
        var settings = new SqlAssistSettings { UseSquareBrackets = useSquareBrackets };

        Assert.Equal(expected, SqlInsertionText.Quote(name, settings));
    }
}
