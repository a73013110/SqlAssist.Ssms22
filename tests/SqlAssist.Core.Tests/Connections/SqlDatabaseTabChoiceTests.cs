using SqlAssist.Core.Connections;
using Xunit;

namespace SqlAssist.Core.Tests.Connections;

public sealed class SqlDatabaseTabChoiceTests
{
    [Theory]
    // 從選單選了「資料庫」。
    [InlineData(false, true, true, SqlDatabaseTabChoiceStep.Remember)]
    // 重開後殼層把設定裡的無著色套回來。
    [InlineData(true, false, true, SqlDatabaseTabChoiceStep.Reapply)]
    // 改選專案、副檔名或規則運算式。
    [InlineData(true, true, false, SqlDatabaseTabChoiceStep.Forget)]
    // 按「無著色」時記憶已先清掉，關掉之後不得再套回來。
    [InlineData(false, false, true, SqlDatabaseTabChoiceStep.None)]
    // 選單切換時，兩個偏好先後變動的中間狀態。
    [InlineData(false, true, false, SqlDatabaseTabChoiceStep.None)]
    [InlineData(true, true, true, SqlDatabaseTabChoiceStep.None)]
    public void NextStepFollowsTheShellPreferences(bool remembered, bool coloring, bool providerIsDatabase, SqlDatabaseTabChoiceStep expected)
    {
        Assert.Equal(expected, SqlDatabaseTabChoice.Next(remembered, coloring, providerIsDatabase));
    }
}
