using SqlAssist.Core;
using Xunit;

namespace SqlAssist.Core.Tests;

/// <summary>
/// 輸入結束詞元的字元之後，要不要把建議清單重開一次。
/// </summary>
/// <remarks>
/// 平台在有 session 時不會回頭問來源，只拿新的文字重新篩選舊清單。結束詞元的字元
/// 會讓上下文換掉，舊清單一定比不中，於是清單默默關掉——那正是使用者說的
/// 「打了 <c>a.</c> 沒反應，要再打一個字母才出現」。
/// </remarks>
public sealed class SqlCompletionTriggerTests
{
    /// <summary>
    /// 用 | 標出游標位置，讓測試讀起來就是使用者看到的畫面。
    /// </summary>
    /// <remarks>
    /// 判斷只吃游標前方的文字，因此標記後面那一段會被切掉——寫出來是為了
    /// 讓每一筆輸入看起來像一句完整的 SQL，而不是半截。
    /// </remarks>
    private static bool ShouldReopen(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "測試輸入必須用 | 標出游標位置。");
        return SqlCompletionTriggers.ShouldReopen(sqlWithCaret.Substring(0, caret));
    }

    [Fact]
    public void 別名後方的點號要重開()
    {
        Assert.True(ShouldReopen("SELECT a.| FROM dbo.PUBLISHER a"));
    }

    [Fact]
    public void 結構描述後方的點號要重開()
    {
        Assert.True(ShouldReopen("SELECT * FROM dbo.|"));
    }

    [Fact]
    public void 方括號限定字後方的點號要重開()
    {
        Assert.True(ShouldReopen("SELECT * FROM [dbo].|"));
    }

    [Fact]
    public void 資料表名稱後方的點號要重開()
    {
        Assert.True(ShouldReopen("SELECT PUBLISHER.| FROM dbo.PUBLISHER"));
    }

    /// <summary>括號內的別名一樣要重開：COUNT( 不是新的查詢範圍。</summary>
    [Fact]
    public void 函式括號內的點號要重開()
    {
        Assert.True(ShouldReopen("SELECT COUNT(a.| FROM dbo.PUBLISHER a"));
        Assert.True(ShouldReopen("SELECT ISNULL(a.|, 0) FROM dbo.PUBLISHER a"));
    }

    /// <summary>
    /// 目標已經收斂的位置，空白鍵也要重開。
    /// </summary>
    /// <remarks>
    /// 這是與點號同一類的問題：使用者打完 <c>FROM</c> 時清單還開著（裡面是關鍵字），
    /// 空白鍵一按，平台拿 <c>FROM </c> 去比對那份清單，比不中就關掉。
    /// 少了這一條，<c>SELECT * FROM |</c> 要再多打一個字母才列得出資料表。
    /// </remarks>
    [Fact]
    public void 目標收斂的關鍵字後方要重開()
    {
        Assert.True(ShouldReopen("SELECT * FROM |"));
        Assert.True(ShouldReopen("SELECT * FROM A a INNER JOIN |"));
        Assert.True(ShouldReopen("EXEC |"));
        Assert.True(ShouldReopen("USE |"));
        Assert.True(ShouldReopen("ALTER PROCEDURE |"));
        Assert.True(ShouldReopen("INSERT INTO |"));
    }

    /// <summary>
    /// 目標沒有收斂就不重開。
    /// </summary>
    /// <remarks>
    /// 這一條與「輸入幾個字元之後才開始建議」是同一個設定在說話：前綴是空的，
    /// 而觸發字元數最少是 1，所以建議來源本來就不會參與。在這裡先擋掉，
    /// 只是省下一次白跑的來回。
    /// </remarks>
    [Fact]
    public void 目標沒收斂就不重開()
    {
        Assert.False(ShouldReopen("SELECT |"));
        Assert.False(ShouldReopen("SELECT COUNT(|"));
        Assert.False(ShouldReopen("SELECT a.X, |"));
        Assert.False(ShouldReopen("SELECT * FROM A a WHERE |"));
    }

    /// <summary>游標不在結束詞元的字元後面就與這件事無關，例如剛打完一個字母。</summary>
    [Fact]
    public void 還在打識別字時不重開()
    {
        Assert.False(ShouldReopen("SELECT a|"));
        Assert.False(ShouldReopen("SELECT a.b|"));
        Assert.False(ShouldReopen("SELECT * FROM CUST|"));
    }

    /// <summary>
    /// 小數點不是限定字。
    /// </summary>
    /// <remarks>
    /// 分不出來的代價是每次輸入小數點都彈出整個資料庫的物件清單。
    /// </remarks>
    [Fact]
    public void 數值的小數點不重開()
    {
        Assert.False(ShouldReopen("SELECT 1.|"));
        Assert.False(ShouldReopen("SELECT * FROM A a WHERE Price > 12.|"));
    }

    [Fact]
    public void 前面沒有限定字的點號不重開()
    {
        Assert.False(ShouldReopen(".|"));
        Assert.False(ShouldReopen("|"));
    }

    /// <summary>
    /// 點號與限定字之間的空白不影響判斷。
    /// </summary>
    /// <remarks>
    /// <c>dbo . PUBLISHER</c> 在 T-SQL 裡是合法的寫法，分析器因此允許空白，
    /// 這裡跟著它走而不是另外訂一套規則——兩套規則遲早會分岔。
    /// </remarks>
    [Fact]
    public void 點號前有空白仍視為限定字()
    {
        Assert.True(ShouldReopen("SELECT * FROM dbo .|"));
    }

    [Fact]
    public void 字串與註解裡不重開()
    {
        Assert.False(ShouldReopen("SELECT 'dbo.|"));
        Assert.False(ShouldReopen("-- dbo.|"));
        Assert.False(ShouldReopen("/* dbo.|"));
        Assert.False(ShouldReopen("SELECT '... FROM |"));
    }

    [Fact]
    public void 空白文字不擲例外()
    {
        Assert.False(SqlCompletionTriggers.ShouldReopen(string.Empty));
        Assert.Throws<System.ArgumentNullException>(
            () => SqlCompletionTriggers.ShouldReopen(null!));
    }
}
