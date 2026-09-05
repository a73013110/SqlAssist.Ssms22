using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Connections;

namespace SqlAssist.Ssms22.Editor;

/// <summary>銜接物件定位與平台載入策略；文字判斷由 SqlObjectLookup 共用。</summary>
internal static class SqlObjectLocator
{
    /// <summary>使用者主動要求的結構面板與 F12，允許等候中繼資料。</summary>
    public static async Task<SqlObjectLocation?> LocateAsync(
        SqlMetadataService metadataService,
        string text,
        int position,
        CancellationToken cancellationToken)
    {
        // 大型貼上腳本的敘述分析也不佔用命令呼叫端的 UI 執行緒。
        var lookup = await Task.Run(() => SqlObjectLookup.Create(text, position), cancellationToken)
            .ConfigureAwait(false);
        if (lookup is null)
        {
            return null;
        }

        var snapshot = await metadataService
            .GetSnapshotAsync(lookup.Reference.Path, cancellationToken)
            .ConfigureAwait(false);
        var candidate = lookup.FindCandidate(snapshot);
        if (candidate is null)
        {
            return null;
        }

        var detail = candidate.NeedsColumn
            ? await metadataService.GetDetailAsync(candidate.Object, cancellationToken).ConfigureAwait(false)
            : null;
        return lookup.Locate(candidate, detail);
    }

    /// <summary>Hover 只取現成資料；不足時交由服務背景預載，不等待資料庫。</summary>
    public static SqlObjectLocation? LocateCached(SqlMetadataService metadataService, SqlObjectLookup lookup)
    {
        var candidate = lookup.FindCandidate(metadataService.PeekSnapshot(lookup.Reference.Path));
        if (candidate is null)
        {
            return null;
        }

        return lookup.Locate(candidate, candidate.NeedsColumn ? metadataService.PeekDetail(candidate.Object) : null);
    }
}
