using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlEditorConnectionTextTests
{
    /// <summary>摘要一定說得出跟著的是哪一個，或根本沒連上；兩個工具窗同一句。</summary>
    [Theory]
    [InlineData("LIBSQL01", "查詢視窗（LIBSQL01）")]
    [InlineData("", "查詢視窗（未連線）")]
    [InlineData(null, "查詢視窗（未連線）")]
    public void 標籤帶著名稱或未連線(string? name, string expected)
    {
        Assert.Equal(expected, SqlEditorConnectionText.Label(name));
    }

    /// <summary>Tooltip 說得出按下去會套到哪一條連線；沒連上時不假裝有目標。</summary>
    [Fact]
    public void 說明寫出會套用的連線()
    {
        Assert.Contains("LIBSQL01 · Library", SqlEditorConnectionText.ApplyToolTip(new SqlConnectionLabel("LIBSQL01", "Library")));
        Assert.Contains("沒有連線", SqlEditorConnectionText.ApplyToolTip(null));
        Assert.Contains("沒有連線", SqlEditorConnectionText.ApplyToolTip(new SqlConnectionLabel("LIBSQL01", "")));
        Assert.StartsWith(SqlEditorConnectionText.ApplyAction, SqlEditorConnectionText.ApplyToolTip(null));
    }
}
