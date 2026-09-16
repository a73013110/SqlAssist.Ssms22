using System;
using System.ComponentModel;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

internal sealed class SqlMemoryRow : INotifyPropertyChanged
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
    public string Status => Favorite is not null ? "收藏" : IsExecuted ? "執行" : "草稿";
    public SqlIcon StatusIcon => IsFavorite ? SqlIcon.Favorite : SqlAssistChrome.MemoryOptionIcon(History!.Kind);
    public SqlIcon ServerIcon => Favorite?.Favorite.Scope == SqlFavoriteScope.Global ? SqlIcon.Global : SqlIcon.Server;
    public bool CanAddFavorite => Favorite is null && RevisionId is not null;
    public string AddFavoriteHint => Favorite is not null ? "已是收藏" : CanAddFavorite ? "新增至收藏" : "未存檔草稿尚無版本，請先開啟為新查詢；目前不能直接收藏。";
    public string DeleteLabel => IsFavorite ? "從收藏移除" : "從 History 刪除";

    private bool _isNew;
    private bool _isRemoving;

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
    public string Server => Favorite?.Favorite.Scope == SqlFavoriteScope.Global ? "全域" :
        (Favorite?.Favorite.Connection ?? History?.Connection)?.Server is { Length: > 0 } server ? server : "無伺服器";
    public string Database => Favorite is { } favorite && favorite.Favorite.Scope != SqlFavoriteScope.Database ? "" :
        (Favorite?.Favorite.Connection ?? History?.Connection)?.Database is { Length: > 0 } database ? database : "無資料庫";
    public string RelativeTime => History is { } history ? SqlMemoryTimeText.RelativeTime(history.CreatedAt, DateTimeOffset.Now) : ScopeName(Favorite!.Favorite.Scope);
    // 收藏契約未提供時間，不用 scope／連線文字冒充時間或再重複顯示。
    public string Timestamp => History is { } history ? history.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz") : "";
    public void RefreshTime() => Changed(nameof(RelativeTime));
    public string Detail => Favorite is { } favorite
        ? $"{ScopeName(favorite.Favorite.Scope)} · {ConnectionText(favorite.Favorite.Connection)}"
        : $"{History!.CreatedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss} · {(History.RevisionId is null ? "未存檔草稿" : History.Kind == SqlHistoryFilter.Executions ? "執行" : "草稿")} · {ConnectionText(History.Connection)}";
    private static string ConnectionText(SqlConnectionLabel? context) => context is null ? "無連線資訊" : $"{context.Server} · {context.Database}";
    public static string ScopeName(SqlFavoriteScope scope) => scope switch
    {
        SqlFavoriteScope.Server => "伺服器", SqlFavoriteScope.Database => "資料庫", _ => "全域"
    };
}
