using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Preview;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 對編輯器裡某一個位置的物件做事：開結構預覽、開定義。
/// </summary>
/// <remarks>
/// 快捷鍵（F12、Ctrl+F12、工具選單）給的是游標，Ctrl＋點擊給的是滑鼠點到的位置，
/// 之後做的事一模一樣。各寫一份的下場是其中一份少了內建名稱那一條退路，
/// 或少了一句失敗訊息。回饋一律走狀態列：這些都綁在按鍵或點擊上，要按確定的
/// 對話框比沒反應更糟。
/// </remarks>
internal static class SqlObjectNavigation
{
    /// <summary>依點擊手勢派送；<see cref="SqlClickAction.None"/> 什麼都不做。</summary>
    public static void Run(SqlClickAction action, IWpfTextView view, SnapshotPoint point, IServiceProvider serviceProvider)
    {
        switch (action)
        {
            case SqlClickAction.ShowStructure:
                ShowStructure(view, point, serviceProvider);
                break;
            case SqlClickAction.GoToDefinition:
                GoToDefinition(view, point, serviceProvider);
                break;
        }
    }

    /// <summary>把 <paramref name="point"/> 處的物件定義開進新的查詢視窗。</summary>
    public static void GoToDefinition(ITextView view, SnapshotPoint point, IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!SqlCompletionServices.GetDefinitionOpener(view, serviceProvider).TryBegin(point))
        {
            SqlAssistStatusBar.Show(serviceProvider, NotRecognized(point));
        }
    }

    /// <summary>在浮動預覽顯示 <paramref name="point"/> 處的物件；內建名稱顯示完整說明。</summary>
    public static void ShowStructure(IWpfTextView view, SnapshotPoint point, IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = ShowStructureAsync(view, point, serviceProvider);
    }

    /// <remarks>
    /// 沒有人接結果的工作，本身不能丟出例外——丟出去就是一個沒有人觀察的 Task 例外，
    /// 而使用者只看到按了沒反應。刻意不走 Guard：那一族是「這一輪安靜地不做」，
    /// 但這是使用者自己要求的，失敗一定要說得出原因。
    /// </remarks>
    private static async Task ShowStructureAsync(IWpfTextView view, SnapshotPoint point, IServiceProvider serviceProvider)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var text = point.Snapshot.GetText();
            var metadataService = SqlCompletionServices.GetMetadataService(view, serviceProvider);

            // 使用者主動要求的路徑，等得起一次查詢。
            var location = await SqlObjectLocator.LocateAsync(
                metadataService,
                text,
                point.Position,
                CancellationToken.None,
                NotificationOrigin.User).ConfigureAwait(true);

            if (view.IsClosed)
            {
                return;
            }

            if (location is null)
            {
                // CONVERT 與 DATEADD 不是資料庫物件，但停在它們上面時要問的事一模一樣：
                // 這個引數可以填什麼。同一個視窗答得出來。
                if (!ShowBuiltIn(view, point.Snapshot, text, point.Position, serviceProvider))
                {
                    SqlAssistStatusBar.Show(serviceProvider, NotRecognized(point));
                }

                return;
            }

            var anchor = point.Snapshot.CreateTrackingSpan(
                new Span(location.Reference.Start, location.Reference.Length),
                SpanTrackingMode.EdgeInclusive);

            if (SqlStructurePreview.GetOrCreate(view, serviceProvider) is { } preview)
            {
                // 暫存資料表、資料表變數與 CTE 的結構在定位那一步就讀出來了；
                // 它們不在中繼資料裡，交給一般載入路徑只會等到一句「沒有可用的連線」。
                preview.ShowAt(
                    anchor,
                    location.Object,
                    metadataService,
                    location.Detail is { } detail ? new SqlObjectStructure(detail) : null);
                return;
            }

            SqlAssistStatusBar.Show(serviceProvider, "查詢視窗已關閉，無法顯示物件結構。");
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"開啟物件結構失敗：{exception}");
            SqlAssistStatusBar.Show(serviceProvider, "開啟物件結構失敗；原因已寫入診斷紀錄檔。");
        }
    }

    /// <summary>
    /// 停在內建函式或型別上時，用同一個視窗顯示它的完整說明。
    /// </summary>
    /// <remarks>
    /// 只有真的裝得滿一個視窗才開（<see cref="SqlBuiltInDoc.DeservesWindow"/>，
    /// 與建議清單那條入口同一條規則）。裝不滿時只剩一個標題，那還不如把「不是可辨識的
    /// 資料庫物件」說清楚——使用者至少知道要換個字試。
    /// </remarks>
    private static bool ShowBuiltIn(
        IWpfTextView view,
        ITextSnapshot snapshot,
        string text,
        int position,
        IServiceProvider serviceProvider)
    {
        var reference = SqlIdentifierScanner.FindAt(text, position);

        if (!SqlBuiltInDocCatalog.TryGetAt(text, reference, out var doc) || !doc.DeservesWindow)
        {
            return false;
        }

        if (SqlStructurePreview.GetOrCreate(view, serviceProvider) is not { } preview)
        {
            return false;
        }

        preview.ShowBuiltInAt(
            snapshot.CreateTrackingSpan(new Span(reference!.Start, reference.Length), SpanTrackingMode.EdgeInclusive),
            doc);
        return true;
    }

    /// <summary>
    /// 說出是哪一個名稱認不得。
    /// </summary>
    /// <remarks>
    /// 「游標處」這個說法在 Ctrl＋點擊時是錯的——游標根本沒動過；寫出名稱兩條路都對。
    /// 只讀那一行：這一句只為了拿名稱，不值得一次整份快照的字串複製。
    /// </remarks>
    private static string NotRecognized(SnapshotPoint point)
    {
        var line = point.GetContainingLine();
        return SqlIdentifierScanner.FindAt(line.GetText(), point.Position - line.Start.Position) is { } reference
            ? $"{reference.Name} 不是可辨識的資料庫物件。"
            : "這個位置沒有可辨識的資料庫物件。";
    }
}
