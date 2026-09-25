using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>游標停在呼叫的括號裡、畫面上卻沒有參數提示時，要不要把它請回來。</summary>
public sealed class SqlParameterHintRevivalTests
{
    private const SqlParameterHintCallState Open = SqlParameterHintCallState.Open;
    private const SqlParameterHintCallState Awaiting = SqlParameterHintCallState.Awaiting;
    private const SqlParameterHintCallState Dismissed = SqlParameterHintCallState.Dismissed;

    private static SqlParameterHintDecision Decide(
        int? previousCall,
        SqlParameterHintCallState previousState,
        int? currentCall,
        SqlParameterHintEdits edits = SqlParameterHintEdits.None,
        bool escaped = false,
        bool hintVisible = false) =>
        SqlParameterHintRevival.Decide(previousCall, previousState, currentCall, edits, escaped, hintVisible);

    [Theory]
    [InlineData(1, "", SqlParameterHintEdits.Deleted)]
    [InlineData(0, "Y", SqlParameterHintEdits.Inserted)]
    [InlineData(2, "YEAR", SqlParameterHintEdits.Inserted)]
    [InlineData(0, ")", SqlParameterHintEdits.None)]
    [InlineData(1, ")", SqlParameterHintEdits.Inserted)]
    [InlineData(0, "", SqlParameterHintEdits.None)]
    public void 編輯分類(int deletedLength, string insertedText, SqlParameterHintEdits expected)
    {
        Assert.Equal(expected, SqlParameterHintRevival.Classify(deletedLength, insertedText));
    }

    /// <remarks>打錯字按 Backspace、在同一個字上點第二下：游標沒有離開那組括號，提示卻不見了。</remarks>
    [Theory]
    [InlineData(SqlParameterHintEdits.Deleted)]
    [InlineData(SqlParameterHintEdits.None)]
    [InlineData(SqlParameterHintEdits.Inserted)]
    public void 同一個呼叫裡提示不見了就請回來(SqlParameterHintEdits edits)
    {
        var decision = Decide(15, Open, 15, edits);

        Assert.True(decision.Summon);
        Assert.Equal(15, decision.Call);
        Assert.Equal(Awaiting, decision.State);
    }

    [Fact]
    public void 同一個呼叫裡提示還開著就不請()
    {
        var decision = Decide(15, Awaiting, 15, SqlParameterHintEdits.Deleted, hintVisible: true);

        Assert.False(decision.Summon);
        Assert.Equal(Open, decision.State);
    }

    /// <remarks><c>INSERT INTO t (</c> 這類括號請了也不會有東西，每停一次手就請一次是白送命令。</remarks>
    [Fact]
    public void 請過卻沒浮出來就停手_刪字才再試()
    {
        Assert.False(Decide(15, Awaiting, 15).Summon);
        Assert.False(Decide(15, Awaiting, 15, SqlParameterHintEdits.Inserted).Summon);
        Assert.True(Decide(15, Awaiting, 15, SqlParameterHintEdits.Deleted).Summon);
    }

    /// <remarks>點回、方向鍵走回、打完內層右括號回到外層；開著的那一份講的是上一個函式。</remarks>
    [Theory]
    [InlineData(null, false)]
    [InlineData(30, false)]
    [InlineData(30, true)]
    public void 走進另一個呼叫就請(int? previousCall, bool hintVisible)
    {
        Assert.True(Decide(previousCall, Awaiting, 15, hintVisible: hintVisible).Summon);
    }

    /// <remarks>打左括號那一條自己請過。</remarks>
    [Fact]
    public void 打字走進呼叫的不重複請()
    {
        var decision = Decide(null, Open, 15, SqlParameterHintEdits.Inserted);

        Assert.False(decision.Summon);
        Assert.Equal(Open, decision.State);
    }

    /// <remarks>提交函式那一條已經請過；再請一次會把剛浮出來的提示收掉重畫。</remarks>
    [Fact]
    public void 別的路徑剛請過就不再請()
    {
        var edits = SqlParameterHintEdits.Summoned | SqlParameterHintEdits.Inserted | SqlParameterHintEdits.Deleted;
        var decision = Decide(30, Open, 15, edits);

        Assert.False(decision.Summon);
        Assert.Equal(15, decision.Call);
        Assert.Equal(Awaiting, decision.State);
    }

    [Fact]
    public void 不在呼叫裡什麼都不記()
    {
        var decision = Decide(15, Dismissed, null, SqlParameterHintEdits.Deleted);

        Assert.False(decision.Summon);
        Assert.Null(decision.Call);
        Assert.Equal(Open, decision.State);
    }

    [Fact]
    public void 按了Esc的呼叫裡刪字也不請()
    {
        var escaped = Decide(15, Open, 15, escaped: true);

        Assert.False(escaped.Summon);
        Assert.Equal(Dismissed, escaped.State);
        Assert.False(Decide(15, escaped.State, 15, SqlParameterHintEdits.Deleted).Summon);
    }

    [Fact]
    public void 換到別的呼叫之後Esc就失效()
    {
        var decision = Decide(15, Dismissed, 40);

        Assert.True(decision.Summon);
        Assert.Equal(Awaiting, decision.State);
    }

    /// <remarks>
    /// 提示浮出來過，之後被收掉就可以再請；Esc 不因為 SSMS 自己浮出來一次就失效。
    /// </remarks>
    [Theory]
    [InlineData(SqlParameterHintCallState.Awaiting, SqlParameterHintCallState.Open)]
    [InlineData(SqlParameterHintCallState.Open, SqlParameterHintCallState.Open)]
    [InlineData(SqlParameterHintCallState.Dismissed, SqlParameterHintCallState.Dismissed)]
    public void 提示浮出來之後的狀態(SqlParameterHintCallState before, SqlParameterHintCallState expected)
    {
        Assert.Equal(expected, SqlParameterHintRevival.Shown(before));
    }
}
