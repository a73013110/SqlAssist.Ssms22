using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 列操作的唯一實作：卡片快速操作、快捷選單、Delete 鍵與 Preview 都走這裡，行為與文案不會分岔。
/// </summary>
/// <remarks>
/// 只負責儲存呼叫、對話框與結果分類；清單怎麼增刪列由 <see cref="Removed"/>／<see cref="Replaced"/> 的訂閱者決定。
/// 每個非同步操作都記下宿主世代，回來時世代換過或已停用就放棄，不讓舊儲存的結果動到新清單。
/// 同一時間只跑一個需要等儲存的操作，避免連按刪除或重複開窗。
/// </remarks>
internal sealed class SqlMemoryItemCommands
{
    private readonly SqlAssistPackage _package;
    private int _busy;

    public SqlMemoryItemCommands(SqlAssistPackage package) => _package = package;

    /// <summary>儲存已確認刪除（或該筆本來就不在了）；訂閱者把列移出清單。</summary>
    public event Action<SqlMemoryRow>? Removed;

    /// <summary>收藏已更新；第二個參數是重讀後的新列，收藏已不存在時為 null。</summary>
    public event Action<SqlMemoryRow, SqlMemoryRow?>? Replaced;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public static bool CanRun(SqlMemoryRowAction action, SqlMemoryRow? row) =>
        row is not null && SqlMemoryHost.Runtime.IsAvailable && SqlMemoryRowCommand.For(action).AppliesTo(row.IsFavorite) &&
        (action != SqlMemoryRowAction.AddFavorite || row.CanAddFavorite);

    /// <param name="source">決定確認框與對話框的擁有者視窗。</param>
    /// <param name="report">操作結果寫到呼叫端自己的狀態列。</param>
    /// <param name="token">呼叫端的頁面生命週期；切頁或停用時取消，晚到的結果不再回報。</param>
    /// <param name="loadedSql">呼叫端已經讀好的全文（Preview）；null 時按需讀取。</param>
    public async Task RunAsync(SqlMemoryRowAction action, SqlMemoryRow row, DependencyObject source, Action<string> report,
        CancellationToken token, string? loadedSql = null)
    {
        if (!CanRun(action, row)) return;
        switch (action)
        {
            case SqlMemoryRowAction.Copy:
            case SqlMemoryRowAction.Open:
                var open = action == SqlMemoryRowAction.Open;
                await WithSqlAsync(row, loadedSql, token, report, open ? "開啟" : "複製", sql =>
                {
                    if (open) SqlMemoryActions.OpenQuery(_package, sql);
                    else Clipboard.SetText(sql);
                    report(open ? "已開啟新查詢；未執行 SQL。" : "已複製完整 SQL。");
                    return Task.CompletedTask;
                });
                break;
            case SqlMemoryRowAction.AddFavorite:
                if (new FavoriteMetadataWindow(_package, row, false).ShowModal() == true) report("已加入收藏；可到 Favorites 查看。");
                break;
            case SqlMemoryRowAction.EditMetadata:
                if (new FavoriteMetadataWindow(_package, row, true).ShowModal() == true)
                    await ReloadFavoriteAsync(row, token, report, "收藏資料已儲存。");
                break;
            case SqlMemoryRowAction.EditSql:
                await WithSqlAsync(row, loadedSql, token, report, "編輯", async sql =>
                {
                    if (new FavoriteSqlEditWindow(_package, row.Favorite!, sql).ShowModal() == true)
                        await ReloadFavoriteAsync(row, token, report, "SQL 已儲存。");
                });
                break;
            case SqlMemoryRowAction.Delete:
                await DeleteAsync(row, source, token, report);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "沒有這個列操作。");
        }
    }

    private async Task DeleteAsync(SqlMemoryRow row, DependencyObject source, CancellationToken token, Action<string> report)
    {
        var owner = Window.GetWindow(source) ?? Application.Current?.MainWindow;
        var confirmed = row.Favorite is { } favorite
            ? SqlAssistConfirmationWindow.Confirm(owner!, "從收藏移除", $"移除收藏「{favorite.Favorite.Name}」？",
                "只移除此收藏，不連帶刪除 History；移除後無法復原。", "從收藏移除")
            : SqlAssistConfirmationWindow.Confirm(owner!, "從 History 刪除", $"刪除「{row.Name}」這筆{row.Status}紀錄？",
                "只刪除這一筆，不影響收藏或其他紀錄；仍被收藏或其他版本使用的 SQL 會保留。" +
                "查詢視窗若仍開著，之後的編輯會再產生新紀錄。刪除後無法復原。", "刪除");
        if (!confirmed) return;

        await WithStorageAsync(token, report, "刪除", async () =>
        {
            if (row.Favorite is { } item)
            {
                var result = await SqlMemoryHost.Runtime.DeleteFavoriteAsync(item.Favorite.FavoriteId, item.Version, token);
                return () =>
                {
                    if (result == SqlFavoriteWriteResult.Conflict) { report("收藏已被修改或刪除；請重新整理後再操作。"); return; }
                    Removed?.Invoke(row);
                    report("收藏已移除；History 未刪除。");
                };
            }

            var deleted = await SqlMemoryHost.Runtime.DeleteHistoryAsync(row.History!, token);
            return () =>
            {
                // 已被維護回收或其他 SSMS 刪掉：目標狀態已達成，照樣移出清單並說明。
                Removed?.Invoke(row);
                report(deleted == SqlHistoryDeleteResult.Deleted ? "已從 History 刪除。" : "這筆紀錄已不存在；已從清單移除。");
            };
        });
    }

    private Task ReloadFavoriteAsync(SqlMemoryRow row, CancellationToken token, Action<string> report, string success) =>
        WithStorageAsync(token, report, "重新讀取收藏", async () =>
        {
            var current = await SqlMemoryHost.Runtime.ReadFavoriteAsync(row.Favorite!.Favorite.FavoriteId, token);
            return () =>
            {
                Replaced?.Invoke(row, current is null ? null : new SqlMemoryRow(current));
                report(current is null ? "收藏已不存在；已從清單移除。" : success);
            };
        });

    private Task WithSqlAsync(SqlMemoryRow row, string? loadedSql, CancellationToken token, Action<string> report, string verb,
        Func<string, Task> use)
    {
        if (loadedSql is not null) return use(loadedSql);
        return WithStorageAsync(token, report, verb, async () =>
        {
            var content = await SqlMemoryHost.Runtime.ReadContentAsync(row.ContentId, token);
            if (content is null) throw new InvalidOperationException("內容已不存在，請重新整理。");
            return () => { _ = SqlMemoryActions.RunAsync(() => use(content.SqlText), report); };
        });
    }

    /// <summary>
    /// 一次等儲存的操作：拒絕重入、記下宿主世代，回來時仍屬於同一份儲存才套用結果。
    /// </summary>
    /// <param name="work">背景部分；回傳在 UI 執行緒套用結果的動作。</param>
    private async Task WithStorageAsync(CancellationToken token, Action<string> report, string verb, Func<Task<Action>> work)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        var generation = SqlMemoryHost.Runtime.Generation;
        Action? apply = null;
        try
        {
            apply = await work();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 切頁或停用後才回來的失敗不能蓋掉新頁面的訊息；取消本來就不回報。
            if (IsCurrent(generation, token)) report(SqlMemoryTimeText.Failure(verb, error));
            return;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
        if (IsCurrent(generation, token)) apply();
    }

    private static bool IsCurrent(long generation, CancellationToken token) =>
        !token.IsCancellationRequested && SqlMemoryHost.Runtime.IsAvailable && generation == SqlMemoryHost.Runtime.Generation;
}
