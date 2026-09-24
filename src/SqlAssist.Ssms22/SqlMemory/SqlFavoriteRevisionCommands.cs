using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 版本操作的唯一實作：時間軸列按鈕、快捷選單、Enter／雙擊與差異面板都走這裡。
/// </summary>
/// <remarks>
/// 回溯沒有專用寫入路徑：讀出舊版本全文，交給 <see cref="ISqlFavoriteStore.SaveFavoriteAsync"/> 另存一筆新版本。
/// 版本是 append-only，舊版本不被改寫或刪除；版本衝突與配額回收沿用改 SQL 的既有語意。
/// 「預覽此版本」只切換畫面，由呼叫端自己處理，不經過儲存。
/// </remarks>
internal sealed class SqlFavoriteRevisionCommands
{
    private readonly SqlAssistPackage _package;
    private readonly SqlMemoryOperationGate _gate = new();

    public SqlFavoriteRevisionCommands(SqlAssistPackage package) => _package = package;

    /// <summary>收藏可能已改變（回溯已提交、版本衝突或回應不明）；訂閱者重讀收藏與時間軸。</summary>
    public event Action? FavoriteChanged;

    public bool IsBusy => _gate.IsBusy;

    public static bool CanRun(SqlFavoriteRevisionAction action, SqlFavoriteRevisionRow? row) =>
        row is not null && SqlMemoryHost.Runtime.IsAvailable && !row.ContentMissing &&
        (action != SqlFavoriteRevisionAction.Revert || row.CanRevert);

    /// <param name="favorite">回溯時的 CAS 基準；必須是開窗後最後一次讀到的收藏。</param>
    /// <param name="retainedLimit">每個收藏保留的版本數；確認框據此說明回溯可能讓最舊的版本被回收。</param>
    /// <param name="loadedSql">呼叫端已經讀好的此版本全文；null 時按需讀取。</param>
    public async Task RunAsync(SqlFavoriteRevisionAction action, SqlFavoriteRevisionRow row, SqlFavoriteItem favorite,
        int retainedLimit, DependencyObject source, Action<string> report, CancellationToken token, string? loadedSql = null)
    {
        if (!CanRun(action, row)) return;
        switch (action)
        {
            case SqlFavoriteRevisionAction.Open:
            case SqlFavoriteRevisionAction.Copy:
                var open = action == SqlFavoriteRevisionAction.Open;
                await WithSqlAsync(row, loadedSql, token, report, open ? "開啟" : "複製", sql =>
                {
                    if (open) SqlMemoryActions.OpenQuery(_package, sql);
                    else Clipboard.SetText(sql);
                    report(open ? "已用此版本開啟新查詢；未執行 SQL。" : "已複製此版本的完整 SQL。");
                });
                break;
            case SqlFavoriteRevisionAction.Revert:
                await RevertAsync(row, favorite, retainedLimit, source, report, token, loadedSql);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "這個版本操作不經過儲存。");
        }
    }

    private async Task RevertAsync(SqlFavoriteRevisionRow row, SqlFavoriteItem favorite, int retainedLimit,
        DependencyObject source, Action<string> report, CancellationToken token, string? loadedSql)
    {
        var owner = SsmsWindows.OwnerOf(source);
        var time = row.Item.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (!SqlAssistConfirmationWindow.Confirm(owner, "回溯為新版本", $"以 {time} 的版本建立新的目前版本？",
                "回溯會把這個版本的 SQL 另存成一筆新版本並設為目前版本；歷史不會倒帶，目前版本與其他舊版本都保留，之後仍可再回溯。" +
                $"每個收藏只保留最近 {retainedLimit.ToString(CultureInfo.InvariantCulture)} 版，多出的最舊版本會在維護時回收。",
                "回溯為新版本"))
            return;

        // 回溯要讀舊全文、另存新版本並重讀時間軸，使用者可能已經切走；成功走通知，
        // 衝突與「不確定有沒有成功」留在視窗裡——那兩句要當場讀完才知道下一步。
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.RestoringFavoriteRevision,
            NotificationKind.SqlMemory, NotificationOrigin.User, NotificationLevel.Info);
        try
        {
            await RevertCoreAsync();
        }
        catch (OperationCanceledException)
        {
            // 視窗關了或換了儲存：卡片不能停在「已回溯」。
            notification.Cancel();
            throw;
        }

        Task RevertCoreAsync() => _gate.RunAsync(token, Failed, "回溯", async () =>
        {
            var sql = loadedSql;
            if (sql is null)
            {
                var content = await SqlMemoryHost.Runtime.ReadContentAsync(row.Item.ContentId, token);
                if (content is null)
                    return () =>
                    {
                        row.ContentMissing = true; row.CanRevert = false;
                        // 卡片不能停在「已回溯」：這一次什麼都沒寫進去。
                        Failed("此版本的內容已被清理");
                        report("此版本的內容已被清理，無法回溯。");
                    };
                sql = content.SqlText;
            }

            // 名稱與標註照舊，只換目前版本；版本衝突由同一個 CAS 擋下。
            var save = new SqlFavoriteSave(favorite.Favorite with { CurrentRevisionId = Guid.NewGuid() }, favorite.Version,
                DateTimeOffset.UtcNow, sql);
            SqlFavoriteWriteResult result;
            try
            {
                result = await SqlMemoryHost.Runtime.SaveFavoriteAsync(save, token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 回應不明時不重送非冪等的回溯；重讀清單讓使用者看得到到底有沒有多出一版。
                return () =>
                {
                    Failed("回溯未確認：" + error.Message);
                    report("回溯未確認：" + error.Message + " 已重新讀取版本清單確認結果；不會自動重送。");
                    FavoriteChanged?.Invoke();
                };
            }
            return () =>
            {
                if (result == SqlFavoriteWriteResult.Committed) notification.Report(time);
                else
                {
                    // 沒有回溯不是失敗：收藏在別處被改過，使用者看清單就知道現在是哪一版。
                    notification.Report("收藏已被修改或移除，未回溯");
                    report("收藏已被修改或移除，未回溯；已重新讀取版本清單。");
                }

                FavoriteChanged?.Invoke();
            };
        });

        void Failed(string message)
        {
            notification.Report(message);
            notification.Fail();
        }
    }

    private Task WithSqlAsync(SqlFavoriteRevisionRow row, string? loadedSql, CancellationToken token, Action<string> report,
        string verb, Action<string> use)
    {
        if (loadedSql is not null) { use(loadedSql); return Task.CompletedTask; }
        return _gate.RunAsync(token, report, verb, async () =>
        {
            var content = await SqlMemoryHost.Runtime.ReadContentAsync(row.Item.ContentId, token);
            if (content is null) return () => { row.ContentMissing = true; row.CanRevert = false; report("此版本的內容已被清理。"); };
            return () => SqlMemoryActions.Run(() => use(content.SqlText), report);
        });
    }
}
