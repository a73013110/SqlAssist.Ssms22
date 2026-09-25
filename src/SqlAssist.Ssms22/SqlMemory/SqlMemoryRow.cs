using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using SqlAssist.Core.Connections;
using SqlAssist.Core.Localization;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Core.Tabular;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

internal sealed class SqlMemoryRow : INotifyPropertyChanged, ISqlCheckableRow
{
    public SqlMemoryRow(SqlHistoryItem history) { History = history; }
    public SqlMemoryRow(SqlFavoriteItem favorite) { Favorite = favorite; }
    public SqlHistoryItem? History { get; }
    public SqlFavoriteItem? Favorite { get; }
    public bool IsFavorite => Favorite is not null;
    public Guid Id => Favorite?.Favorite.FavoriteId ?? History!.ItemId;
    public Guid? RevisionId => Favorite?.Favorite.CurrentRevisionId ?? History?.RevisionId;
    public string ContentId => Favorite?.ContentId ?? History!.ContentId;
    public string Name => Favorite?.Favorite.Name ?? History!.DisplayName;
    public string Preview => (Favorite?.Preview ?? History!.Preview).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsExecuted => History?.Kind == SqlHistoryFilter.Executions;
    public string Status => Favorite is not null ? SqlMemoryUiText.FavoriteStatusLabel : SqlMemoryCopy.HistoryStatus(History!);
    public SqlIcon StatusIcon => IsFavorite ? SqlIcon.Favorite : SqlAssistChrome.MemoryOptionIcon(History!.Kind);
    public string DeleteLabel => IsFavorite ? SqlMemoryCommandText.RemoveFromFavorites : SqlMemoryCommandText.DeleteFromHistory;

    /// <summary>History 以建立時間、收藏以最後儲存時間排序；列上的時間與清單順序同源。</summary>
    public DateTimeOffset Time => Favorite?.UpdatedAt ?? History!.CreatedAt;

    private bool _isNew;
    private bool _isRemoving;
    private bool _isChecked;

    /// <summary>多選勾起來了；由清單的選取控制器依 <see cref="Id"/> 設定，容器重用時樣板只讀這一份。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; Changed(nameof(IsChecked)); }
    }

    /// <summary>剛加入清單；卡片以它播一次進場動畫，清單稍後清掉，捲動重用容器時才不會重播。</summary>
    public bool IsNew
    {
        get => _isNew;
        set { if (_isNew == value) return; _isNew = value; Changed(nameof(IsNew)); }
    }

    /// <summary>已確認刪除、正在離場；刪除失敗時設回 false，卡片從當下狀態回復。</summary>
    public bool IsRemoving
    {
        get => _isRemoving;
        set { if (_isRemoving == value) return; _isRemoving = value; Changed(nameof(IsRemoving)); }
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    // 收藏的標註是選填：沒有標註就不畫膠囊（空字串），History 沒有連線則明講。
    public string Server => Favorite is { } favorite ? favorite.Favorite.Server ?? "" :
        History!.Connection?.Server is { Length: > 0 } server ? server : SqlMemoryUiText.NoServerLabel;
    public string Database => Favorite is { } favorite ? favorite.Favorite.Database ?? "" :
        History!.Connection?.Database is { Length: > 0 } database ? database : SqlMemoryUiText.NoDatabaseLabel;
    public string RelativeTime => SqlMemoryTimeText.RelativeTime(Time, DateTimeOffset.Now);
    public string Timestamp => Time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz");

    /// <summary>連續相同執行併成一列的次數膠囊；只有一次時是空字串，卡片與 Preview 收起膠囊。</summary>
    public string ExecutionCountText => History is { ExecutionCount: > 1 } item ? "×" + SqlText.Number(item.ExecutionCount) : "";

    public string ExecutionCountToolTip => History is { ExecutionCount: > 1 } item
        ? SqlMemoryUiText.RepeatedExecutionsTooltip(SqlText.Number(item.ExecutionCount)) : "";

    /// <summary>Preview 資訊列的時間；合併的執行列同時交代首次與最後一次，單次仍只顯示一個時間。</summary>
    public string TimeSummary => History is { ExecutionCount: > 1, FirstExecutedAt: { } first }
        ? SqlMemoryUiText.FirstAndLastTime(first.ToLocalTime(), Time.ToLocalTime())
        : Timestamp;
    public void RefreshTime() => Changed(nameof(RelativeTime));
    public string Detail => Favorite is { } item
        ? SqlMemoryUiText.FavoriteDetail(Time.ToLocalTime(), TagText(item.Favorite))
        : $"{TimeSummary} · {(History!.RevisionId is null ? SqlMemoryUiText.UnsavedDraftLabel : IsExecuted ? ExecutionText(History.ExecutionCount) : SqlMemoryUiText.DraftLabel)} · {ConnectionText(History.Connection)}";
    private static string ExecutionText(int count) => count > 1 ? SqlMemoryUiText.ExecutedCount(SqlText.Number(count)) : SqlMemoryUiText.ExecutedLabel;
    private static string ConnectionText(SqlConnectionLabel? context) => context is null ? SqlMemoryUiText.NoConnectionInfo : $"{context.Server} · {context.Database}";
    private static string TagText(SqlFavorite favorite) => favorite.Server is null && favorite.Database is null
        ? SqlMemoryUiText.UntaggedConnection
        : $"{favorite.Server ?? SqlMemoryUiText.AnyServerLabel} · {favorite.Database ?? SqlMemoryUiText.AnyDatabaseLabel}";

    /// <summary>
    /// 多選複製的內容：依傳進來的順序（清單的顯示順序），欄位是 History 或 Favorites 那一份。
    /// </summary>
    /// <remarks>只讀列上已有的資料，不讀 SQL 全文，也不觸發預覽讀取。</remarks>
    public static SqlTabularContent CopyContent(IEnumerable<SqlMemoryRow> rows, bool favorites) => favorites
        ? SqlTabularText.Build(SqlMemoryCopy.FavoriteColumns, rows.Where(row => row.Favorite is not null).Select(row => row.Favorite!))
        : SqlTabularText.Build(SqlMemoryCopy.HistoryColumns, rows.Where(row => row.History is not null).Select(row => row.History!));
}
