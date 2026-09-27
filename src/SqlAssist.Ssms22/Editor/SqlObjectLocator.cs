using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Connections;

namespace SqlAssist.Ssms22.Editor;

/// <summary>銜接物件定位與平台載入策略；文字判斷由 SqlObjectLookup 共用。</summary>
internal static class SqlObjectLocator
{
    /// <summary>使用者主動要求的結構面板與 F12，允許等候中繼資料。</summary>
    /// <param name="origin">
    /// 誰觸發的。這條路徑同時服務 F12（<see cref="NotificationOrigin.User"/>）與
    /// 打字時的參數提示（<see cref="NotificationOrigin.Typing"/>），底下的中繼資料
    /// 查詢該用哪一種降噪門檻只有呼叫端知道。
    /// </param>
    public static async Task<SqlObjectLocation?> LocateAsync(
        SqlMetadataService metadataService,
        string text,
        int position,
        CancellationToken cancellationToken,
        NotificationOrigin origin)
    {
        // 大型貼上腳本的敘述分析也不佔用命令呼叫端的 UI 執行緒。
        var lookup = await Task.Run(() => SqlObjectLookup.Create(text, position), cancellationToken)
            .ConfigureAwait(false);
        if (lookup is null)
        {
            return null;
        }

        // 指令碼換過資料庫時先確認連線：F12 與參數提示都跑在背景工作上，等得起
        // 一次往返，而拿另一個資料庫裡同名的物件回答會開出完全無關的定義。
        await metadataService.ConfirmConnectionAsync().ConfigureAwait(false);

        var snapshot = await metadataService
            .GetSnapshotAsync(lookup.Reference.Path, cancellationToken)
            .ConfigureAwait(false);

        // 敘述裡指名別的資料庫時，那個目錄的第一層也要載齊：使用者主動按下的路徑
        // 等得起查詢，而少了這一輪，跨庫來源的欄位答不答得出來只取決於快取剛好
        // 有沒有載過——同一個 c.CopyNo 有時有 F12，有時說不是可辨識的物件。
        foreach (var external in lookup.FindExternalSources())
        {
            await metadataService.GetSnapshotAsync(external, cancellationToken).ConfigureAwait(false);
        }

        // 第一輪只用現成的明細：絕大多數的位置根本不必看欄位，看得到的那些多半也
        // 已經在快取裡（建議清單載過同一份）。
        var candidate = lookup.FindCandidate(snapshot, metadataService.PeekDetail, metadataService.PeekSnapshot);

        if (candidate is null)
        {
            // 沒有答案的位置才可能是未限定的欄位。使用者主動按下的路徑等得起查詢，
            // 把這條敘述的資料來源明細補齊再判斷一次；少了這一輪，同一個欄位
            // F12 得到的答案會取決於快取剛好有沒有載過。
            candidate = await LocateColumnAsync(metadataService, lookup, snapshot, cancellationToken, origin)
                .ConfigureAwait(false);
        }

        if (candidate is null && lookup.TryGetSystemFallbackSchema(out var fallbackSchema))
        {
            // 使用者自己的同名物件已經在上面兩輪找過；只有兩者都落空，才問系統物件——
            // sys.sp_helpindex、sys.dm_exec_requests 這類限定字答不出來，是因為第一層
            // 快照刻意不收系統物件；未限定的 sp_help 找不到使用者物件時，也是把
            // SQL Server 名稱解析本來的規則搬過來。使用者主動按下的路徑等得起這一輪查詢。
            candidate = await LocateSystemObjectAsync(metadataService, lookup, fallbackSchema, cancellationToken)
                .ConfigureAwait(false);
        }

        if (candidate is null)
        {
            return null;
        }

        // 指令碼宣告的物件在候選人身上就帶著明細；再問中繼資料一次是白跑的查詢。
        var detail = candidate.ScriptDetail is null && candidate.NeedsColumn
            ? await metadataService.GetDetailAsync(candidate.Object, cancellationToken, origin).ConfigureAwait(false)
            : null;
        return lookup.Locate(candidate, detail);
    }

    /// <summary>把敘述裡的資料來源明細載齊，讓未限定的欄位有得比對。</summary>
    /// <remarks>
    /// 只在前一輪什麼都沒找到時走到這裡，而且明細本來就會進第二層快取——
    /// 同一條敘述問第二次不會再查一次資料庫。
    /// </remarks>
    private static async Task<SqlObjectLookup.Candidate?> LocateColumnAsync(
        SqlMetadataService metadataService,
        SqlObjectLookup lookup,
        SqlDatabaseSnapshot? snapshot,
        CancellationToken cancellationToken,
        NotificationOrigin origin)
    {
        var sources = lookup.FindColumnSources(snapshot, metadataService.PeekSnapshot);

        if (sources.Count == 0)
        {
            return null;
        }

        // 物件實例來自同一份快照，比對用參考相等就夠。
        var details = new Dictionary<SqlObjectInfo, SqlObjectDetail>();

        foreach (var source in sources)
        {
            if (await metadataService.GetDetailAsync(source, cancellationToken, origin).ConfigureAwait(false) is { } detail)
            {
                details[source] = detail;
            }
        }

        return details.Count == 0
            ? null
            : lookup.FindCandidate(
                snapshot,
                owner => details.TryGetValue(owner, out var detail) ? detail : null,
                metadataService.PeekSnapshot);
    }

    /// <summary>
    /// 限定字是系統結構描述、或未限定的 sp_／xp_ 名稱找不到使用者物件時的退路。
    /// </summary>
    /// <remarks>
    /// 走的是與 <c>FROM sys.triggers</c> 同一支
    /// <see cref="SqlMetadataCatalog.FindObjectsAsync"/>，不是另外拼一次規則：
    /// 系統物件第一次被問到才查資料庫，之後整個工作階段都命中快取。
    /// </remarks>
    private static async Task<SqlObjectLookup.Candidate?> LocateSystemObjectAsync(
        SqlMetadataService metadataService,
        SqlObjectLookup lookup,
        string schemaName,
        CancellationToken cancellationToken)
    {
        var catalog = ScopeToPath(metadataService.PeekCurrentConnection()?.Catalog, lookup.Reference.Path);

        if (catalog is null)
        {
            return null;
        }

        var matches = await catalog
            .FindObjectsAsync(lookup.Reference.Name, schemaName, cancellationToken)
            .ConfigureAwait(false);

        return lookup.ToCandidate(matches);
    }

    /// <summary>Hover 只取現成資料；不足時交由服務背景預載，不等待資料庫。</summary>
    public static SqlObjectLocation? LocateCached(SqlMetadataService metadataService, SqlObjectLookup lookup)
    {
        var snapshot = metadataService.PeekSnapshot(lookup.Reference.Path);

        var candidate = lookup.FindCandidate(
            snapshot,
            owner => PeekOrWarm(metadataService, owner),
            metadataService.PeekSnapshot);

        if (candidate is null)
        {
            candidate = PeekSystemCandidateOrWarm(metadataService, lookup, snapshot);
        }

        if (candidate is null)
        {
            return null;
        }

        var detail = candidate.ScriptDetail is null && candidate.NeedsColumn
            ? metadataService.PeekDetail(candidate.Object)
            : null;
        return lookup.Locate(candidate, detail);
    }

    /// <summary>
    /// 系統物件的退路只讀快取；沒有就排一次背景預載，不等查詢。
    /// </summary>
    /// <remarks>
    /// 系統物件併回同一份快照之後，一般的 <see cref="SqlObjectLookup.FindCandidate"/>
    /// 對限定字（<c>sys.sp_helpindex</c>）就已經答得出來——<see cref="SqlDatabaseSnapshot.Find"/>
    /// 本來就會在限定字是系統結構描述時去問 <see cref="SqlDatabaseSnapshot.SystemObjects"/>。
    /// 這裡只補兩種情形：那一份還沒載入，以及未限定的 sp_／xp_ 名稱——後者即使系統物件
    /// 已經載入，<c>Find</c> 也不會主動去問（沒有限定字時一律只查使用者物件），
    /// 這裡是那個例外唯一的入口。只在 <see cref="SqlObjectLookup.TryGetSystemFallbackSchema"/>
    /// 成立時才排背景預載——不是每一次 Hover 落空都排。
    /// </remarks>
    private static SqlObjectLookup.Candidate? PeekSystemCandidateOrWarm(
        SqlMetadataService metadataService,
        SqlObjectLookup lookup,
        SqlDatabaseSnapshot? snapshot)
    {
        if (!lookup.TryGetSystemFallbackSchema(out var schemaName))
        {
            return null;
        }

        var matches = snapshot?.Find(lookup.Reference.Name, schemaName) ?? Array.Empty<SqlObjectInfo>();

        if (matches.Count > 0)
        {
            return lookup.ToCandidate(matches);
        }

        WarmSystemObjects(metadataService, lookup.Reference.Path);
        return null;
    }

    /// <summary>
    /// 在背景把系統物件併進快照；下一次停留就答得出來，這一輪不等它。
    /// </summary>
    /// <remarks>
    /// 同一個目錄不會重複排查詢：<see cref="SqlMetadataCatalog.GetSystemObjectsAsync"/>
    /// 自己就是「查過一次就用快取到編輯器關閉」的一次性載入（先看
    /// <c>Volatile.Read</c> 的快取，沒有才進只放行一個呼叫的 <c>SemaphoreSlim</c>），
    /// 滑鼠在同一個字上晃動反覆呼叫這裡，實際查詢仍然只有一輪，這裡不必再疊一層
    /// 進行中旗標。
    /// </remarks>
    private static void WarmSystemObjects(SqlMetadataService metadataService, SqlObjectPath? path)
    {
        var catalog = ScopeToPath(metadataService.PeekCurrentConnection()?.Catalog, path);

        if (catalog is null)
        {
            return;
        }

        SqlAssistPlatformGuard.BeginProbe(
            "預先載入系統物件",
            () => catalog.GetSystemObjectsAsync(CancellationToken.None));
    }

    /// <summary>
    /// 把目錄換成限定字指名的伺服器與資料庫；沒有限定字或就在目前這條連線上時原樣回傳。
    /// </summary>
    /// <remarks>
    /// 系統物件的退路要跳過第一層快照、直接問目錄，而目前這條連線的目錄只有
    /// <see cref="SqlMetadataService.PeekCurrentConnection"/> 交得出來——它不接受路徑，
    /// 換庫換伺服器要靠 <see cref="SqlMetadataCatalogRegistry"/> 既有的 <c>ScopeTo</c> 規則，
    /// 與 <see cref="SqlMetadataService"/> 內部同名的私有方法做同一件事，不另寫一份右對齊或
    /// 跨庫比對。
    /// </remarks>
    private static SqlMetadataCatalog? ScopeToPath(SqlMetadataCatalog? catalog, SqlObjectPath? path)
    {
        if (catalog is null || path is null || path.IsLocal)
        {
            return catalog;
        }

        return SqlMetadataCatalogRegistry.Default.ScopeTo(catalog, path.DatabaseName, path.ServerName);
    }

    /// <summary>
    /// 未限定的欄位要比對來源明細，而 Hover 不等查詢。
    /// </summary>
    /// <remarks>
    /// 快取沒有就排一次背景預載，下一次停留即可辨識——與物件明細缺席時同一套做法。
    /// 預載本身會擋掉重複的在途查詢，滑鼠在同一個字上晃動不會變成連續查詢。
    /// </remarks>
    private static SqlObjectDetail? PeekOrWarm(SqlMetadataService metadataService, SqlObjectInfo owner)
    {
        if (metadataService.PeekDetail(owner) is { } detail)
        {
            return detail;
        }

        metadataService.WarmDetail(owner);
        return null;
    }
}
