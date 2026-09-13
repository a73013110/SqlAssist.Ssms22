using System;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.Ssms22.QueryMemory;

internal sealed class QueryMemoryRow
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
    public string Detail => Saved is { } saved
        ? $"{(saved.Query.Pinned ? "已標記 · " : "")}{ScopeName(saved.Query.Scope)} · {ConnectionText(saved.Query.Connection)}"
        : $"{History!.CreatedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss} · {(History.RevisionId is null ? "未存檔草稿" : History.Kind == QueryHistoryKind.Executed ? "執行" : "草稿")} · {ConnectionText(History.Connection)}";
    private static string ConnectionText(QueryConnectionContext? context) => context is null ? "無連線資訊" : $"{context.Server} · {context.Database}";
    public static string ScopeName(SavedQueryScope scope) => scope switch
    {
        SavedQueryScope.Server => "伺服器", SavedQueryScope.Database => "資料庫", _ => "全域"
    };
}
