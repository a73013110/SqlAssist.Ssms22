using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Completion;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Completion;

/// <summary>
/// 建議清單裡選到的項目，浮動預覽要拿它畫什麼。
/// </summary>
/// <remarks>
/// 浮動預覽的入口裡，只有建議清單這一條拿不到現成的
/// <see cref="SqlObjectInfo"/>——指令碼自己宣告的名稱在中繼資料裡查不到，內建名稱
/// 根本不是物件，項目上帶的是宣告本身或什麼都沒有。認錯的症狀是使用者按向右鍵
/// 得到「沒有可以顯示的內容」，而那個名稱是他上一行才寫下的、或者正是他要查
/// style 對照表的 <c>CONVERT</c>。
/// </remarks>
public sealed class SqlSuggestionTargetTests
{
    private static SqlSuggestion Suggestion(string name, SuggestionKind kind, object? tag = null) =>
        new(name, name, "說明", name, kind, tag: tag);

    private static SqlScriptTable Declaration(string name) =>
        SqlScriptTableCollector.Collect(
            SqlTokenizer.Tokenize($"DECLARE {name} TABLE (CopyNo NVARCHAR(20))"))[name];

    /// <summary>資料庫物件早就掛在項目上，原封不動交出去。</summary>
    [Fact]
    public void 資料庫物件直接沿用項目上的描述()
    {
        var objectInfo = new SqlObjectInfo(42, "dbo", "Lib_Reader", SqlObjectKind.Table);

        Assert.Same(
            objectInfo,
            SqlSuggestionTarget.Describe(
                Suggestion("Lib_Reader", SuggestionKind.Table, objectInfo))!.Object);
    }

    /// <summary>指令碼自己宣告的三種都認得出來，而且說得出是哪一種。</summary>
    [Theory]
    [InlineData("#Loan", SqlObjectKind.TemporaryTable)]
    [InlineData("##Loan", SqlObjectKind.TemporaryTable)]
    [InlineData("@rows", SqlObjectKind.TableVariable)]
    [InlineData("c", SqlObjectKind.CommonTableExpression)]
    public void 指令碼宣告的資料來源認得出種類(string name, SqlObjectKind kind)
    {
        var target = SqlSuggestionTarget.Describe(
            Suggestion(name, SuggestionKind.ScriptDataSource))!.Object!;

        Assert.Equal(name, target.Name);
        Assert.Equal(kind, target.Kind);

        // 編號一律是 0，種類必須說得出它其實不在中繼資料裡——第二、三層快取
        // 正是照編號存的，放行的症狀是兩個宣告互相蓋掉對方的欄位。
        Assert.Equal(0, target.ObjectId);
        Assert.True(target.Kind.IsScriptDeclared());
    }

    /// <summary>
    /// 資料表變數認得，一般變數不認。
    /// </summary>
    /// <remarks>
    /// 兩者在清單裡長得一模一樣，唯一的差別是項目有沒有帶著一份讀得出資料行的宣告。
    /// 一律放行的症狀是停在 <c>@readerId</c> 上按向右鍵，跳出一個空的結構視窗。
    /// </remarks>
    [Fact]
    public void 只有帶著宣告的變數有結構可看()
    {
        var target = SqlSuggestionTarget.Describe(
            Suggestion("@rows", SuggestionKind.Variable, Declaration("@rows")))!.Object!;

        Assert.Equal(SqlObjectKind.TableVariable, target.Kind);
        Assert.Null(SqlSuggestionTarget.Describe(Suggestion("@readerId", SuggestionKind.Variable)));
    }

    /// <summary>
    /// 內建名稱換得到那一份說明，種類照項目自己帶的。
    /// </summary>
    /// <remarks>
    /// 種類不從文字再猜一次：<c>YEAR</c> 在日期部分與內建函式目錄裡各有一筆，猜的話
    /// 兩邊都說得通，而症狀是把函式的範例貼到日期部分上。
    /// </remarks>
    [Theory]
    [InlineData("CONVERT", SuggestionKind.BuiltInFunction, SqlBuiltInKind.Function)]
    [InlineData("NVARCHAR", SuggestionKind.DataType, SqlBuiltInKind.DataType)]
    [InlineData("YEAR", SuggestionKind.DatePart, SqlBuiltInKind.DatePart)]
    [InlineData("NOLOCK", SuggestionKind.TableHint, SqlBuiltInKind.TableHint)]
    [InlineData("@@ROWCOUNT", SuggestionKind.GlobalVariable, SqlBuiltInKind.GlobalVariable)]
    public void 內建名稱換得到說明(string name, SuggestionKind kind, SqlBuiltInKind expected)
    {
        var doc = SqlSuggestionTarget.Describe(Suggestion(name, kind))!.BuiltIn!;

        Assert.Equal(name, doc.Name);
        Assert.Equal(expected, doc.Kind);
    }

    /// <summary>
    /// 裝不滿一個視窗的內建名稱不開。
    /// </summary>
    /// <remarks>
    /// 一行說明使用者已經在說明面板上看著了，為它蓋一個視窗上去等於把那一行遮掉。
    /// 對照表或範例才是向右鍵要開的東西——與 Ctrl+F12 同一條規則。
    /// </remarks>
    [Fact]
    public void 只有一行說明的內建名稱不值得一個視窗()
    {
        Assert.True(SqlBuiltInDocCatalog.TryGet("SMALLINT", SqlBuiltInKind.DataType, out var doc));
        Assert.True(doc.HasContent);
        Assert.False(doc.DeservesWindow);

        Assert.Null(SqlSuggestionTarget.Describe(Suggestion("SMALLINT", SuggestionKind.DataType)));
    }

    /// <summary>其餘的項目沒有東西可畫，回 null 讓預覽說實情。</summary>
    [Theory]
    [InlineData(SuggestionKind.Keyword)]
    [InlineData(SuggestionKind.Snippet)]
    [InlineData(SuggestionKind.Schema)]
    public void 其餘項目沒有東西可畫(SuggestionKind kind)
    {
        Assert.Null(SqlSuggestionTarget.Describe(Suggestion("SELECT", kind)));
    }
}
