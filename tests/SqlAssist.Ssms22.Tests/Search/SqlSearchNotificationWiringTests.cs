using System;
using System.IO;
using System.Linq;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

/// <summary>
/// SQL Search 的回饋通道接線：沒有狀態列，說明清單的話在頁尾，按下去的結果走通知。
/// </summary>
/// <remarks>
/// 瀏覽器、預覽與啟動要 SSMS 的服務才跑得起來，不編進測試；頁尾的文案在
/// <c>SqlSearchBrowserModelTests</c>，這裡看的是接線。
/// </remarks>
public sealed class SqlSearchNotificationWiringTests
{
    private const string Browser = "Search/SqlSearchBrowser.cs";
    private const string Preview = "Search/SqlSearchPreview.cs";
    private const string Activation = "Search/SqlSearchActivation.cs";

    /// <summary>工具窗與預覽都不再有狀態列；筆數與部分結果由清單頁尾說。</summary>
    [Fact]
    public void 工具窗與預覽沒有狀態列()
    {
        foreach (var path in new[] { Browser, Preview })
        {
            var source = ReadProductSource(path);
            Assert.DoesNotContain("CreateStatusText", source, StringComparison.Ordinal);
            Assert.DoesNotContain("void Report(", source, StringComparison.Ordinal);
        }

        var browser = ReadProductSource(Browser);
        Assert.Contains("_list.SetRowsSource(_rows, _footer);", browser, StringComparison.Ordinal);
        Assert.Contains("_footer.Update(_model.Footer(_rows.Count));", browser, StringComparison.Ordinal);
    }

    /// <summary>按下去的結果走通知，種類是 SQL Search；移至定義與物件總管選取由啟動那一支自己送。</summary>
    [Fact]
    public void 按下去的結果走通知()
    {
        var browser = ReadProductSource(Browser);
        Assert.Contains("NotificationCenter.Default.Post(title, NotificationKind.Search, NotificationOrigin.User, NotificationLevel.Info,",
            browser, StringComparison.Ordinal);
        foreach (var title in new[]
                 {
                     "NotificationCatalog.CopyingQualifiedName", "NotificationCatalog.CopyingSqlList",
                     "NotificationCatalog.ApplyingEditorConnection", "NotificationCatalog.SearchingDatabaseObjects",
                     "NotificationCatalog.RunningSearchAction",
                 })
            Assert.Contains(title, browser, StringComparison.Ordinal);

        var preview = ReadProductSource(Preview);
        Assert.Contains("NotificationCatalog.HighlightingMatches", preview, StringComparison.Ordinal);
        // 複製定義成功也要說一句：按下去畫面上沒有變化。
        Assert.Contains("SqlSearchBrowser.Notify(NotificationCatalog.CopyingDefinition,", preview, StringComparison.Ordinal);
        Assert.Contains("failure is null ? NotificationStatus.Succeeded : NotificationStatus.Failed, subject,", preview, StringComparison.Ordinal);

        // 被擋下的那幾種也有一則通知，與進行中的那一則同一個標題。
        var activation = ReadProductSource(Activation);
        Assert.Contains("Reject(NotificationCatalog.GoingToDefinition,", activation, StringComparison.Ordinal);
        Assert.Contains("const string title = NotificationCatalog.SelectingInObjectExplorer;", activation, StringComparison.Ordinal);
    }

    /// <summary>預覽的列操作從清單那一份篩出來、同一個順序，交回清單那一條路執行。</summary>
    [Fact]
    public void 預覽的列操作與清單同一份()
    {
        var preview = ReadProductSource(Preview);
        Assert.Contains("foreach (var command in SqlSearchRowCommand.Preview) _tools.Children.Add(CreateRowAction(command, runAction));",
            preview, StringComparison.Ordinal);
        Assert.Contains("new SqlSearchPreview(_catalogs, action => Run(() => RunRowAction(action)))",
            ReadProductSource(Browser), StringComparison.Ordinal);
    }

    /// <summary>左鍵點一下就是預覽，列操作裡沒有同義的「在預覽中顯示」；順序即三處的呈現順序。</summary>
    [Fact]
    public void 列操作依序是移至定義選取與複製()
    {
        Assert.Equal(
            new[] { SqlSearchRowAction.Activate, SqlSearchRowAction.SelectInExplorer, SqlSearchRowAction.Copy },
            SqlSearchRowCommand.All.Select(command => command.Action).ToArray());
        Assert.True(SqlSearchRowCommand.All[0].IsPrimary);
    }

    /// <summary>預覽上只有一顆複製：作用在畫面上的定義；複製限定名稱留在清單上，免得兩顆同圖示並排。</summary>
    [Fact]
    public void 預覽不放複製限定名稱()
    {
        Assert.Equal(
            new[] { SqlSearchRowAction.Activate, SqlSearchRowAction.SelectInExplorer },
            SqlSearchRowCommand.Preview.Select(command => command.Action).ToArray());
    }

    private static string ReadProductSource(string relativePath) =>
        File.ReadAllText(Path.Combine(ProductRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string ProductRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "SqlAssist.Ssms22");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("src/SqlAssist.Ssms22");
    }
}
