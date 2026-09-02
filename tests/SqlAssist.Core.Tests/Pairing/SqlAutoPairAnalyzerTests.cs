using SqlAssist.Core.Pairing;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Pairing;

/// <summary>
/// 輸入分隔字元時要不要補上另一半。
/// </summary>
/// <remarks>
/// 這一組規則跑在<b>每一次</b>按鍵上，所以反方向比正方向重要：多補一個字元
/// 使用者當場就得回頭刪掉，而那是他打字的節奏被打斷的唯一原因。
/// 每一個「不補」的案例都對應一次會讓人想把功能關掉的情境。
/// </remarks>
public sealed class SqlAutoPairAnalyzerTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan WHERE |", '(', ')')]
    [InlineData("SELECT COUNT|", '(', ')')]
    [InlineData("SELECT * FROM dbo.Loan WHERE Status = |", '\'', '\'')]
    [InlineData("SELECT * FROM dbo.Loan WHERE Status = N|", '\'', '\'')]
    [InlineData("SELECT * FROM |", '[', ']')]
    [InlineData("SELECT * FROM |", '"', '"')]

    // 逗號、分號與右括號是邊界：中間補一格、或再包一層都要成立。
    [InlineData("VALUES (1, |, 3)", '\'', '\'')]
    [InlineData("SELECT dbo.fn_DueDate(|)", '(', ')')]
    [InlineData("SELECT 1 FROM dbo.Loan WHERE LoanId IN |;", '(', ')')]
    public void 邊界處補上另一半(string sqlWithCaret, char typed, char expected)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.Equal(
            expected,
            SqlAutoPairAnalyzer.AutoCloseFor(new SqlStringText(input.Text), input.Caret, typed));
    }

    /// <remarks>
    /// 右邊還有字時補上的那一半會被夾在中間，使用者接著打的每一個字都在配對外面：
    /// <c>(|CopyNo = 1</c> 打完是 <c>()CopyNo = 1</c>。這是最刺眼的一種誤補。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan WHERE |CopyNo = 1", '(')]
    [InlineData("SELECT |LoanId FROM dbo.Loan", '\'')]
    [InlineData("SELECT 1 |+ 2", '(')]
    public void 右邊不是邊界就不補(string sqlWithCaret, char typed)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.Null(
            SqlAutoPairAnalyzer.AutoCloseFor(new SqlStringText(input.Text), input.Caret, typed));
    }

    /// <remarks>
    /// 字串、註解與方括號識別字裡面的括號與引號都是內容而不是語法，
    /// 補上另一半等於竄改字面值。判斷與建議清單共用同一份語彙狀態。
    /// </remarks>
    [Theory]
    [InlineData("SELECT 'Loan | 1'", '(')]
    [InlineData("-- 借閱紀錄 |", '(')]
    [InlineData("/* 借閱紀錄 | */", '\'')]
    [InlineData("SELECT [Loan | ]", '(')]
    public void 字串與註解裡不配對(string sqlWithCaret, char typed)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.Null(
            SqlAutoPairAnalyzer.AutoCloseFor(new SqlStringText(input.Text), input.Caret, typed));
    }

    /// <summary>不成對的字元一個都不碰。</summary>
    [Theory]
    [InlineData(')')]
    [InlineData(']')]
    [InlineData('<')]
    [InlineData('a')]
    [InlineData(' ')]
    public void 只有開頭字元會觸發配對(char typed)
    {
        Assert.Null(SqlAutoPairAnalyzer.AutoCloseFor(new SqlStringText("SELECT "), 7, typed));
    }

    /// <summary>
    /// 按鍵路徑的第一道篩選與後面每一條規則認得的字元必須是同一組。
    /// </summary>
    /// <remarks>
    /// 篩選漏掉一個字元，那個字元的配對就整組不會發生，而且沒有任何徵兆——
    /// 呼叫端在問到規則之前就已經回頭了。
    /// </remarks>
    [Theory]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('\'')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('"')]
    public void 第一道篩選認得每一個配對字元(char value)
    {
        Assert.True(SqlDelimiterPairs.IsPairCharacter(value));
    }

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('a')]
    [InlineData(',')]
    public void 第一道篩選不放行其他字元(char value)
    {
        Assert.False(SqlDelimiterPairs.IsPairCharacter(value));
    }

    /// <remarks>
    /// 引號要跳得過去，所以這一條刻意不看語彙狀態——<c>'abc|'</c> 的游標
    /// 就在字串裡，而使用者要收掉的正是那個字串。
    /// </remarks>
    [Theory]
    [InlineData("SELECT dbo.fn_DueDate(|)", ')')]
    [InlineData("SELECT 'Overdue|'", '\'')]
    [InlineData("SELECT [Lib_Reader|]", ']')]
    public void 結尾字元就在右邊時跳過它(string sqlWithCaret, char typed)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.True(
            SqlAutoPairAnalyzer.ShouldOvertype(new SqlStringText(input.Text), input.Caret, typed));
    }

    [Theory]
    [InlineData("SELECT dbo.fn_DueDate(|)", ']')]
    [InlineData("SELECT dbo.fn_DueDate(1|)", '(')]
    [InlineData("SELECT COUNT(*)|", ')')]
    public void 右邊不是同一個結尾字元就照常插入(string sqlWithCaret, char typed)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.False(
            SqlAutoPairAnalyzer.ShouldOvertype(new SqlStringText(input.Text), input.Caret, typed));
    }

    [Theory]
    [InlineData("SELECT COUNT(|)", true)]
    [InlineData("SELECT N'|'", true)]
    [InlineData("SELECT [|]", true)]
    [InlineData("SELECT COUNT(*|)", false)]
    [InlineData("SELECT COUNT()|", false)]
    [InlineData("|SELECT 1", false)]
    public void 空配對只認緊鄰的兩個字元(string sqlWithCaret, bool expected)
    {
        var input = SqlWithCaret.Parse(sqlWithCaret);

        Assert.Equal(
            expected,
            SqlAutoPairAnalyzer.IsEmptyPair(new SqlStringText(input.Text), input.Caret));
    }

    /// <remarks>
    /// 包夾不看選取範圍右邊有什麼——使用者已經明確指出要包哪一段。
    /// <c>WHERE |CopyNo| = 1</c> 選起來打左括號，要的就是 <c>(CopyNo) = 1</c>，
    /// 而同一個位置沒有選取時是不補的。
    /// </remarks>
    [Fact]
    public void 有選取範圍時不看右邊的邊界()
    {
        var input = SqlWithCaret.Parse("SELECT * FROM dbo.Loan WHERE |CopyNo = 1");
        var sql = new SqlStringText(input.Text);

        Assert.Equal(')', SqlAutoPairAnalyzer.SurroundCloseFor(sql, input.Caret, '('));
        Assert.Null(SqlAutoPairAnalyzer.AutoCloseFor(sql, input.Caret, '('));
    }

    [Fact]
    public void 註解裡的選取範圍不包夾()
    {
        var input = SqlWithCaret.Parse("-- |借閱紀錄");

        Assert.Null(
            SqlAutoPairAnalyzer.SurroundCloseFor(new SqlStringText(input.Text), input.Caret, '('));
    }
}
