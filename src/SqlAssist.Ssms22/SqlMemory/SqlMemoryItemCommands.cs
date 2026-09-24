using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 列操作的唯一實作：卡片快速操作、快捷選單、Delete 鍵、Preview 與多選刪除都走這裡，行為與文案不會分岔。
/// </summary>
/// <remarks>
/// 只負責儲存呼叫、對話框與結果；清單怎麼增刪列由 <see cref="Removed"/>／<see cref="Replaced"/> 的訂閱者決定。
/// 結果一律走通知（<see cref="SqlMemoryActions.Notify"/>）：效果在工具窗外面（剪貼簿、新查詢、儲存），
/// 而刪除後選取會移到下一筆，寫在工具窗裡的那一句說的是哪一筆就對不上了。
/// 每個非同步操作都記下宿主世代，回來時世代換過或已停用就放棄，不讓舊儲存的結果動到新清單。
/// 同一時間只跑一個需要等儲存的操作，避免連按刪除或重複開窗。
/// </remarks>
internal sealed class SqlMemoryItemCommands
{
    /// <summary>收藏成功的說明；查詢視窗那條入口也用同一句。</summary>
    public const string FavoritesHint = "可到 Favorites 查看。";

    private readonly SqlAssistPackage _package;
    private readonly SqlMemoryOperationGate _gate = new();

    public SqlMemoryItemCommands(SqlAssistPackage package) => _package = package;

    /// <summary>儲存已確認刪除（或該筆本來就不在了）；訂閱者把列移出清單。</summary>
    public event Action<SqlMemoryRow>? Removed;

    /// <summary>收藏已更新；第二個參數是重讀後的新列，收藏已不存在時為 null。</summary>
    public event Action<SqlMemoryRow, SqlMemoryRow?>? Replaced;

    public bool IsBusy => _gate.IsBusy;

    public static bool CanRun(SqlMemoryRowAction action, SqlMemoryRow? row) =>
        row is not null && SqlMemoryHost.Runtime.IsAvailable && SqlMemoryRowCommand.For(action).AppliesTo(row.IsFavorite);

    /// <param name="source">決定確認框與對話框的擁有者視窗。</param>
    /// <param name="token">呼叫端的頁面生命週期；切頁或停用時取消，晚到的結果不再回報。</param>
    /// <param name="loadedSql">呼叫端已經讀好的全文（Preview）；null 時按需讀取。</param>
    public async Task RunAsync(SqlMemoryRowAction action, SqlMemoryRow row, DependencyObject source, CancellationToken token,
        string? loadedSql = null)
    {
        if (!CanRun(action, row)) return;
        switch (action)
        {
            case SqlMemoryRowAction.Copy:
                await WithSqlAsync(row, loadedSql, token, NotificationCatalog.CopyingSql, "複製", async sql =>
                {
                    var failure = await SqlClipboard.WriteAsync(new DataObject(DataFormats.UnicodeText, sql)).ConfigureAwait(true);
                    SqlMemoryActions.Notify(NotificationCatalog.CopyingSql,
                        failure is null ? NotificationStatus.Succeeded : NotificationStatus.Failed, row.Name, failure ?? "");
                }).ConfigureAwait(true);
                break;
            case SqlMemoryRowAction.Open:
                await WithSqlAsync(row, loadedSql, token, NotificationCatalog.OpeningQueryWindow, "開啟", sql =>
                {
                    var failure = SqlMemoryActions.TryOpenQuery(_package, sql);
                    SqlMemoryActions.Notify(NotificationCatalog.OpeningQueryWindow,
                        failure is null ? NotificationStatus.Succeeded : NotificationStatus.Failed, row.Name,
                        failure ?? "未執行 SQL。");
                    return Task.CompletedTask;
                }).ConfigureAwait(true);
                break;
            case SqlMemoryRowAction.AddFavorite:
                // 編輯器要顯示全文；有版本時 SQL 沒改就引用那一份，未存檔草稿則讓收藏自己建一份。
                await WithSqlAsync(row, loadedSql, token, NotificationCatalog.AddingFavorite, "收藏", sql =>
                {
                    var summary = row.RevisionId is null
                        ? "收藏這筆未存檔草稿目前的內容。"
                        : $"收藏這筆{row.Status}紀錄的 SQL；未修改就沿用同一份版本，修改後另存為收藏自己的版本。";
                    if (FavoriteEditorWindow.Create(_package, sql, row.Name, row.History!.Connection, row.RevisionId, summary))
                        SqlMemoryActions.Notify(NotificationCatalog.AddingFavorite, NotificationStatus.Succeeded, row.Name, FavoritesHint);
                    return Task.CompletedTask;
                }).ConfigureAwait(true);
                break;
            case SqlMemoryRowAction.Edit:
                await WithSqlAsync(row, loadedSql, token, NotificationCatalog.SavingFavorite, "編輯", async sql =>
                {
                    if (FavoriteEditorWindow.Edit(_package, row.Favorite!, sql))
                        await ReloadFavoriteAsync(row, token, NotificationCatalog.SavingFavorite).ConfigureAwait(true);
                }).ConfigureAwait(true);
                break;
            case SqlMemoryRowAction.Revisions:
                // 回溯本身已經有「回溯收藏版本」那一則通知；這裡只把列換成新的目前版本。
                if (FavoriteRevisionsWindow.Show(_package, row.Favorite!))
                    await ReloadFavoriteAsync(row, token, null).ConfigureAwait(true);
                break;
            case SqlMemoryRowAction.Delete:
                var deletion = row.Favorite is { } favorite
                    ? SqlMemoryDeletion.Of(new[] { favorite })
                    : SqlMemoryDeletion.Of(new[] { row.History! });
                if (_gate.IsBusy || !ConfirmDelete(source, deletion, row, truncated: false)) return;
                var report = await DeleteAsync(deletion, row, null, token, CancellationToken.None).ConfigureAwait(true);
                if (report is not null && report.Removed.Contains(row.Id)) Removed?.Invoke(row);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "沒有這個列操作。");
        }
    }

    /// <summary>
    /// 刪除（呼叫前先 <see cref="ConfirmDelete"/>）；單筆（<paramref name="row"/> 不是 null）與多選共用通知與儲存呼叫。
    /// </summary>
    /// <returns>有別的操作在跑、失敗或儲存換了世代時是 null；使用者按取消時仍回傳已刪掉的那一部分。</returns>
    /// <param name="progress">多選時每處理完一批回報累計筆數。</param>
    /// <param name="token">頁面生命週期；取消時結果不再套用到畫面。</param>
    /// <param name="stop">使用者按了取消：停在兩批之間，已刪的照實回報。</param>
    public async Task<SqlMemoryDeleteReport?> DeleteAsync(SqlMemoryDeletion deletion, SqlMemoryRow? row,
        IProgress<int>? progress, CancellationToken token, CancellationToken stop)
    {
        if (deletion.Count == 0 || _gate.IsBusy) return null;

        var title = deletion.IsFavorites ? NotificationCatalog.RemovingFavorite : NotificationCatalog.DeletingSqlHistory;
        var verb = deletion.IsFavorites ? "移除" : "刪除";
        using var notification = NotificationCenter.Default.Begin(title, NotificationKind.SqlMemory,
            NotificationOrigin.User, NotificationLevel.Info, row?.Name ?? "");
        var total = Count(deletion.Count);
        var reported = new Progress<int>(done =>
        {
            notification.Report($"已{verb} {Count(done)}/{total} 筆");
            progress?.Report(done);
        });
        SqlMemoryDeleteReport? result = null;
        var failed = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop);
        await _gate.RunAsync(token, message =>
        {
            failed = true;
            notification.Report(message);
            notification.Fail();
        }, verb, async () =>
        {
            var report = await SqlMemoryHost.Runtime.DeleteAsync(deletion, deletion.Count > 1 ? reported : null, linked.Token)
                .ConfigureAwait(true);
            return () => result = report;
        }).ConfigureAwait(true);

        if (result is null)
        {
            // 切頁、停用或換了儲存才回來：那一件事沒有在這個畫面上做完。
            if (!failed) notification.Cancel();
            return null;
        }

        notification.Report(result.Summary);
        if (result.IsCanceled) notification.Cancel();
        else if (result.Conflicts > 0)
        {
            if (result.Deleted == 0) notification.Fail();
            else notification.Degrade();
        }

        return result;
    }

    /// <summary>刪除前的確認；取消是預設按鈕。多選時說出這一次會動到的確切筆數。</summary>
    /// <param name="source">決定確認框的擁有者視窗。</param>
    /// <param name="truncated">「全部符合」超過上限、只讀進前面那一批；確認框要說出來。</param>
    public static bool ConfirmDelete(DependencyObject source, SqlMemoryDeletion deletion, SqlMemoryRow? row, bool truncated)
    {
        var owner = SsmsWindows.OwnerOf(source);
        if (row is not null)
        {
            return row.Favorite is { } favorite
                ? SqlAssistConfirmationWindow.Confirm(owner, "從收藏移除", $"移除收藏「{favorite.Favorite.Name}」？",
                    "只移除此收藏，不連帶刪除 History；移除後無法復原。", "從收藏移除")
                : SqlAssistConfirmationWindow.Confirm(owner, "從 History 刪除", $"刪除「{row.Name}」這筆{row.Status}紀錄？",
                    "只刪除這一筆，不影響收藏或其他紀錄；仍被收藏或其他版本使用的 SQL 會保留。" +
                    "查詢視窗若仍開著，之後的編輯會再產生新紀錄。刪除後無法復原。", "刪除");
        }

        var count = Count(deletion.Count);
        // 只讀進上限那一批時先說清楚：確認框上的筆數就是這一次會動到的筆數，不是符合條件的全部。
        var limit = truncated
            ? $"符合的項目超過 {Count(SqlMemoryBulk.Limit)} 筆，這一次只處理前 {count} 筆；其餘留在清單上。"
            : "";
        return deletion.IsFavorites
            ? SqlAssistConfirmationWindow.Confirm(owner, "從收藏移除", $"移除勾選的 {count} 筆收藏？",
                limit + "只移除收藏，不連帶刪除 History；移除後無法復原。", "從收藏移除")
            : SqlAssistConfirmationWindow.Confirm(owner, "從 History 刪除", $"刪除勾選的 {count} 筆紀錄？",
                limit + "不影響收藏；仍被收藏或其他版本使用的 SQL 會保留。" +
                "查詢視窗若仍開著，之後的編輯會再產生新紀錄。刪除後無法復原。", "刪除");
    }

    /// <param name="title">成功時的通知標題；null 表示不另外通知（例如回溯已經有自己的那一則）。</param>
    private Task ReloadFavoriteAsync(SqlMemoryRow row, CancellationToken token, string? title) =>
        _gate.RunAsync(token, message => Fail(title ?? NotificationCatalog.SavingFavorite, row, message), "重新讀取收藏", async () =>
        {
            var current = await SqlMemoryHost.Runtime.ReadFavoriteAsync(row.Favorite!.Favorite.FavoriteId, token);
            return () =>
            {
                Replaced?.Invoke(row, current is null ? null : new SqlMemoryRow(current));
                if (title is null) return;
                SqlMemoryActions.Notify(title, NotificationStatus.Succeeded, current?.Favorite.Name ?? row.Name,
                    current is null ? "收藏已不存在；已從清單移除。" : "");
            };
        });

    /// <param name="title">失敗時的通知標題。</param>
    /// <param name="verb">失敗訊息的動作名稱。</param>
    private Task WithSqlAsync(SqlMemoryRow row, string? loadedSql, CancellationToken token, string title, string verb,
        Func<string, Task> use)
    {
        Action<string> fail = message => Fail(title, row, message);
        if (loadedSql is not null) return SqlMemoryActions.RunAsync(() => use(loadedSql), fail);
        return _gate.RunAsync(token, fail, verb, async () =>
        {
            var content = await SqlMemoryHost.Runtime.ReadContentAsync(row.ContentId, token);
            if (content is null) throw new InvalidOperationException("內容已不存在，請重新整理。");
            return () => { _ = SqlMemoryActions.RunAsync(() => use(content.SqlText), fail); };
        });
    }

    private static void Fail(string title, SqlMemoryRow row, string message) =>
        SqlMemoryActions.Notify(title, NotificationStatus.Failed, row.Name, message);

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
