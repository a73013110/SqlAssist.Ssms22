using SqlAssist.Core.Completion;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 提交一個使用者自訂函式時補到哪一步。
/// </summary>
/// <remarks>
/// 這一份判斷有兩個呼叫端（提交時要不要補括號、展開時要不要去查參數），
/// 所以答案只能有一個來源；各判斷一次的症狀是兩邊都補。
/// </remarks>
public sealed class SqlFunctionCallInsertionTests
{
    private static SqlAssistSettings Settings(bool parentheses, bool arguments) => new()
    {
        ExpandFunctionCall = parentheses,
        ExpandFunctionArguments = arguments
    };

    /// <remarks>
    /// 括號在 T-SQL 裡不是選擇性的，所以這一格是預設值：
    /// 補括號開著、填引數關著。
    /// </remarks>
    [Fact]
    public void 預設只補一對空括號()
    {
        Assert.Equal(
            SqlFunctionCallInsertionMode.Parentheses,
            SqlFunctionCallInsertion.Resolve(
                isFunction: true,
                CompletionTarget.Any,
                new SqlAssistSettings()));
    }

    [Fact]
    public void 填引數開著時連引數一起補()
    {
        Assert.Equal(
            SqlFunctionCallInsertionMode.Arguments,
            SqlFunctionCallInsertion.Resolve(true, CompletionTarget.Any, Settings(true, true)));
    }

    /// <remarks>括號都不補的話沒有地方可以放引數，所以上層的開關說了算。</remarks>
    [Fact]
    public void 補括號關掉時連引數也不補()
    {
        Assert.Equal(
            SqlFunctionCallInsertionMode.None,
            SqlFunctionCallInsertion.Resolve(true, CompletionTarget.Any, Settings(false, true)));
    }

    /// <remarks>
    /// <c>ALTER</c>／<c>DROP FUNCTION</c> 是宣告位置，補上括號會讓那句 DDL 語法錯誤。
    /// </remarks>
    [Fact]
    public void 宣告位置一律只要名稱()
    {
        Assert.Equal(
            SqlFunctionCallInsertionMode.None,
            SqlFunctionCallInsertion.Resolve(true, CompletionTarget.Function, Settings(true, true)));
    }

    /// <remarks>
    /// <c>CROSS APPLY</c> 是呼叫位置，與 <see cref="CompletionTarget.Function"/> 分開的
    /// 理由就在這裡：分不開的話資料表值函式會少掉括號。
    /// </remarks>
    [Fact]
    public void APPLY是呼叫位置照樣補括號()
    {
        Assert.Equal(
            SqlFunctionCallInsertionMode.Parentheses,
            SqlFunctionCallInsertion.Resolve(true, CompletionTarget.TableFunction, Settings(true, false)));
    }

    [Fact]
    public void 不是函式就什麼都不補()
    {
        Assert.Equal(
            SqlFunctionCallInsertionMode.None,
            SqlFunctionCallInsertion.Resolve(false, CompletionTarget.Any, Settings(true, true)));
    }
}
