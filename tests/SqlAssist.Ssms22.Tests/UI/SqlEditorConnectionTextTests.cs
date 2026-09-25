using SqlAssist.Core.Connections;
using SqlAssist.Core.Localization;
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

    /// <summary>英文介面：名稱、Tooltip 與共用元件的計數用句型避開單複數，不拼字尾。</summary>
    [Fact]
    public void 英文介面的連線說明與計數()
    {
        using (SqlText.Use(SqlLanguage.Find("en")!))
        {
            Assert.Equal("Apply query window connection", SqlEditorConnectionText.ApplyAction);
            Assert.Equal(
                "Apply query window connection: LIBSQL01 · Library. Changes the scope only; the SSMS connection stays the same.",
                SqlEditorConnectionText.ApplyToolTip(new SqlConnectionLabel("LIBSQL01", "Library")));
            Assert.Equal("Rows: 1,234 (header row included; paste straight into Excel).", SqlClipboard.CopiedNote(1234));
            Assert.Equal("Server, Database", SqlFilterSummary.Detail(new[] { "Server", "Database" }));
        }

        Assert.Equal("伺服器、資料庫", SqlFilterSummary.Detail(new[] { "伺服器", "資料庫" }));
    }
}
