using System;
using System.ComponentModel;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Ssms22.QueryMemory;

internal sealed class QueryMemoryRow : INotifyPropertyChanged
{
    public QueryMemoryRow(QueryHistoryItem history) { History = history; }
    public QueryMemoryRow(FavoriteQueryEntry favorite) { Favorite = favorite; }
    public QueryHistoryItem? History { get; }
    public FavoriteQueryEntry? Favorite { get; }
    public bool IsFavorite => Favorite is not null;
    public Guid Id => Favorite?.Query.FavoriteQueryId ?? History!.ItemId;
    public Guid? RevisionId => Favorite?.Query.CurrentRevisionId ?? History?.RevisionId;
    public string ContentId => Favorite?.ContentId ?? History!.ContentId;
    public string Name => Favorite?.Query.Name ?? History!.DisplayName;
    public string Preview => (Favorite?.Preview ?? History!.Preview).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsExecuted => History?.Kind == QueryHistoryKind.Executed;
    public string Status => Favorite is not null ? "收藏" : IsExecuted ? "執行" : "草稿";
    public bool CanAddFavorite => Favorite is null && RevisionId is not null;
    public string AddFavoriteHint => Favorite is not null ? "已是收藏" : CanAddFavorite ? "Add to Favorites" : "未存檔草稿尚無版本，請先開啟為新查詢；目前不能直接收藏。";
    public string Server => Favorite?.Query.Scope == FavoriteQueryScope.Global ? "全域" :
        (Favorite?.Query.Connection ?? History?.Connection)?.Server is { Length: > 0 } server ? server : "無伺服器";
    public string Database => Favorite is { } favorite && favorite.Query.Scope != FavoriteQueryScope.Database ? "" :
        (Favorite?.Query.Connection ?? History?.Connection)?.Database is { Length: > 0 } database ? database : "無資料庫";
    public string RelativeTime => History is { } history ? QueryMemoryPresentation.RelativeTime(history.CreatedAt, DateTimeOffset.Now) : ScopeName(Favorite!.Query.Scope);
    public string Timestamp => History is { } history ? history.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz") : Detail;
    public void RefreshTime() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RelativeTime)));
    public string Detail => Favorite is { } favorite
        ? $"{ScopeName(favorite.Query.Scope)} · {ConnectionText(favorite.Query.Connection)}"
        : $"{History!.CreatedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss} · {(History.RevisionId is null ? "未存檔草稿" : History.Kind == QueryHistoryKind.Executed ? "執行" : "草稿")} · {ConnectionText(History.Connection)}";
    private static string ConnectionText(QueryConnectionContext? context) => context is null ? "無連線資訊" : $"{context.Server} · {context.Database}";
    public static string ScopeName(FavoriteQueryScope scope) => scope switch
    {
        FavoriteQueryScope.Server => "伺服器", FavoriteQueryScope.Database => "資料庫", _ => "全域"
    };
}
