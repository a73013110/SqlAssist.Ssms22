using SqlAssist.Core.Connections;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlEditorConnectionTextTests
{
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
