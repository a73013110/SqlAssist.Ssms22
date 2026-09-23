using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using SqlAssist.Core.Connections;
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
    public string Status => Favorite is not null ? "收藏" : SqlMemoryCopy.HistoryStatus(History!);
    public SqlIcon StatusIcon => IsFavorite ? SqlIcon.Favorite : SqlAssistChrome.MemoryOptionIcon(History!.Kind);
    public string DeleteLabel => IsFavorite ? "從收藏移除" : "從 History 刪除";

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
        History!.Connection?.Server is { Length: > 0 } server ? server : "無伺服器";
    public string Database => Favorite is { } favorite ? favorite.Favorite.Database ?? "" :
        History!.Connection?.Database is { Length: > 0 } database ? database : "無資料庫";
    public string RelativeTime => SqlMemoryTimeText.RelativeTime(Time, DateTimeOffset.Now);
    public string Timestamp => Time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz");

    /// <summary>連續相同執行併成一列的次數膠囊；只有一次時是空字串，卡片與 Preview 收起膠囊。</summary>
    public string ExecutionCountText => History is { ExecutionCount: > 1 } item ? "×" + item.ExecutionCount.ToString(CultureInfo.CurrentCulture) : "";

    public string ExecutionCountToolTip => History is { ExecutionCount: > 1 } item
        ? $"連續執行 {item.ExecutionCount.ToString(CultureInfo.CurrentCulture)} 次" : "";

    /// <summary>Preview 資訊列的時間；合併的執行列同時交代首次與最後一次，單次仍只顯示一個時間。</summary>
    public string TimeSummary => History is { ExecutionCount: > 1, FirstExecutedAt: { } first }
        ? $"首次 {first.ToLocalTime():yyyy/MM/dd HH:mm:ss} · 最後 {Time.ToLocalTime():yyyy/MM/dd HH:mm:ss}"
        : Timestamp;
    public void RefreshTime() => Changed(nameof(RelativeTime));
    public string Detail => Favorite is { } item
        ? $"{Time.ToLocalTime():yyyy/MM/dd HH:mm:ss} 更新 · {TagText(item.Favorite)}"
        : $"{TimeSummary} · {(History!.RevisionId is null ? "未存檔草稿" : IsExecuted ? ExecutionText(History.ExecutionCount) : "草稿")} · {ConnectionText(History.Connection)}";
    private static string ExecutionText(int count) => count > 1 ? $"執行 {count.ToString(CultureInfo.CurrentCulture)} 次" : "執行";
    private static string ConnectionText(SqlConnectionLabel? context) => context is null ? "無連線資訊" : $"{context.Server} · {context.Database}";
    private static string TagText(SqlFavorite favorite) => favorite.Server is null && favorite.Database is null
        ? "未標註伺服器與資料庫"
        : $"{favorite.Server ?? "任何伺服器"} · {favorite.Database ?? "任何資料庫"}";

    /// <summary>
    /// 多選複製的內容：依傳進來的順序（清單的顯示順序），欄位是 History 或 Favorites 那一份。
    /// </summary>
    /// <remarks>只讀列上已有的資料，不讀 SQL 全文，也不觸發預覽讀取。</remarks>
    public static SqlTabularContent CopyContent(IEnumerable<SqlMemoryRow> rows, bool favorites) => favorites
        ? SqlTabularText.Build(SqlMemoryCopy.FavoriteColumns, rows.Where(row => row.Favorite is not null).Select(row => row.Favorite!))
        : SqlTabularText.Build(SqlMemoryCopy.HistoryColumns, rows.Where(row => row.History is not null).Select(row => row.History!));
}
