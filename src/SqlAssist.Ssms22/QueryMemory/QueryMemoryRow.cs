using System;
using System.ComponentModel;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Ssms22.QueryMemory;

internal sealed class QueryMemoryRow : INotifyPropertyChanged
{
    public QueryMemoryRow(QueryHistoryItem history) { History = history; }
    public QueryMemoryRow(SavedQueryEntry saved) { Saved = saved; }
    public QueryHistoryItem? History { get; }
    public SavedQueryEntry? Saved { get; }
    public Guid Id => Saved?.Query.SavedQueryId ?? History!.ItemId;
    public Guid? RevisionId => Saved?.Query.CurrentRevisionId ?? History?.RevisionId;
    public string ContentId => Saved?.ContentId ?? History!.ContentId;
    public string Name => Saved?.Query.Name ?? History!.DisplayName;
    public string Preview => Saved?.Preview ?? History!.Preview;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsExecuted => History?.Kind == QueryHistoryKind.Executed;
    public string Status => Saved is { } saved ? (saved.Query.Pinned ? "收藏 · 已標記" : "收藏") : IsExecuted ? "執行" : "草稿";
    public bool CanSave => Saved is null && RevisionId is not null;
    public string SaveHint => Saved is not null ? "已是收藏" : CanSave ? "加入收藏" : "未存檔草稿尚無版本，請先開啟為新查詢；目前不能直接收藏。";
    public string Server => Saved?.Query.Scope == SavedQueryScope.Global ? "全域" :
        (Saved?.Query.Connection ?? History?.Connection)?.Server is { Length: > 0 } server ? server : "無伺服器";
    public string Database => Saved is { } saved && saved.Query.Scope != SavedQueryScope.Database ? "" :
        (Saved?.Query.Connection ?? History?.Connection)?.Database is { Length: > 0 } database ? database : "無資料庫";
    public string RelativeTime => History is { } history ? QueryMemoryPresentation.RelativeTime(history.CreatedAt, DateTimeOffset.Now) : ScopeName(Saved!.Query.Scope);
    public string Timestamp => History is { } history ? history.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz") : Detail;
    public void RefreshTime() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RelativeTime)));
    public string Detail => Saved is { } saved
        ? $"{(saved.Query.Pinned ? "已標記 · " : "")}{ScopeName(saved.Query.Scope)} · {ConnectionText(saved.Query.Connection)}"
        : $"{History!.CreatedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss} · {(History.RevisionId is null ? "未存檔草稿" : History.Kind == QueryHistoryKind.Executed ? "執行" : "草稿")} · {ConnectionText(History.Connection)}";
    private static string ConnectionText(QueryConnectionContext? context) => context is null ? "無連線資訊" : $"{context.Server} · {context.Database}";
    public static string ScopeName(SavedQueryScope scope) => scope switch
    {
        SavedQueryScope.Server => "伺服器", SavedQueryScope.Database => "資料庫", _ => "全域"
    };
}
