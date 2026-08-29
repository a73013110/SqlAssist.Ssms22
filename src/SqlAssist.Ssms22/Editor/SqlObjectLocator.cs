using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Connections;

namespace SqlAssist.Ssms22.Editor;

/// <summary>游標位置所指的資料庫物件，以及（若游標停在欄位上）該欄位。</summary>
internal sealed class SqlObjectLocation
{
    public SqlObjectLocation(
        SqlIdentifierReference reference,
        SqlObjectInfo objectInfo,
        SqlColumnInfo? column = null)
    {
        Reference = reference;
        Object = objectInfo;
        Column = column;
    }

    public SqlIdentifierReference Reference { get; }

    /// <summary>物件本身；游標停在欄位上時，是該欄位所屬的物件。</summary>
    public SqlObjectInfo Object { get; }

    /// <summary>游標停在欄位上時的欄位描述，否則為 null。</summary>
    public SqlColumnInfo? Column { get; }
}

/// <summary>
/// 把文字中某個位置解析成資料庫物件。
/// </summary>
/// <remarks>
/// 滑鼠停留提示與結構面板共用同一套判斷，否則兩者對同一個位置會給出不同答案。
/// 兩者的差別只在願不願意等資料庫：面板是使用者主動要求的，等得起；
/// 提示在滑鼠移動的路徑上，只用快取裡現成的資料。
/// </remarks>
internal static class SqlObjectLocator
{
    /// <summary>允許查詢資料庫的完整解析，供結構面板使用。</summary>
    public static async Task<SqlObjectLocation?> LocateAsync(
        SqlMetadataService metadataService,
        string text,
        int position,
        CancellationToken cancellationToken)
    {
        var reference = SqlIdentifierScanner.FindAt(text, position);

        if (reference is null)
        {
            return null;
        }

        var snapshot = await metadataService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (snapshot is null || ResolveCandidate(snapshot, text, position, reference) is not { } candidate)
        {
            return null;
        }

        if (!candidate.NeedsColumn)
        {
            return new SqlObjectLocation(reference, candidate.Object);
        }

        var detail = await metadataService
            .GetDetailAsync(candidate.Object, cancellationToken)
            .ConfigureAwait(false);

        return BuildColumnLocation(reference, candidate.Object, detail);
    }

    /// <summary>
    /// 只用已經在快取裡的中繼資料解析，絕不觸發查詢。
    /// </summary>
    /// <remarks>
    /// 明細還沒進快取時仍回報物件本身，呼叫端可以先顯示標題，
    /// 同時請 <see cref="SqlMetadataService.WarmDetail"/> 在背景補上。
    /// </remarks>
    public static SqlObjectLocation? LocateCached(
        SqlMetadataService metadataService,
        string text,
        int position)
    {
        var reference = SqlIdentifierScanner.FindAt(text, position);

        if (reference is null)
        {
            return null;
        }

        var snapshot = metadataService.PeekSnapshot();

        if (snapshot is null || ResolveCandidate(snapshot, text, position, reference) is not { } candidate)
        {
            return null;
        }

        return candidate.NeedsColumn
            ? BuildColumnLocation(reference, candidate.Object, metadataService.PeekDetail(candidate.Object))
            : new SqlObjectLocation(reference, candidate.Object);
    }

    /// <summary>解析出「這個位置指向哪個物件」，以及還缺不缺欄位明細。</summary>
    private readonly struct LocationCandidate
    {
        public LocationCandidate(SqlObjectInfo objectInfo, bool needsColumn)
        {
            Object = objectInfo;
            NeedsColumn = needsColumn;
        }

        public SqlObjectInfo Object { get; }

        /// <summary>游標停的是這個物件的欄位，還要有明細才知道是哪一個。</summary>
        public bool NeedsColumn { get; }
    }

    /// <summary>
    /// 只看文字與第一層中繼資料就能做完的那一段解析。
    /// </summary>
    /// <remarks>
    /// 兩個入口的差別只在願不願意等資料庫，判斷本身必須一模一樣——各寫一份控制流程
    /// 的下場是滑鼠提示與結構面板對同一個位置給出不同答案，而那正是這個類別要防的事。
    /// </remarks>
    private static LocationCandidate? ResolveCandidate(
        SqlDatabaseSnapshot snapshot,
        string text,
        int position,
        SqlIdentifierReference reference)
    {
        var scope = SqlScopeAnalyzer.Analyze(text, position);

        // 限定詞指向敘述中的資料來源時，游標停的是欄位而不是物件。
        if (TryResolveColumnOwner(snapshot, scope, reference, out var owner))
        {
            return new LocationCandidate(owner, needsColumn: true);
        }

        var matches = ResolveObject(snapshot, scope, reference);

        return matches.Count == 0 ? null : new LocationCandidate(matches[0], needsColumn: false);
    }

    /// <summary>判斷這個參考是不是「敘述中某個資料來源的欄位」，是的話取出該資料來源。</summary>
    private static bool TryResolveColumnOwner(
        SqlDatabaseSnapshot snapshot,
        SqlStatementScope scope,
        SqlIdentifierReference reference,
        out SqlObjectInfo owner)
    {
        owner = null!;

        if (reference.Qualifier is null ||
            !scope.TryResolve(reference.Qualifier, out var table) ||
            table.IsDerived)
        {
            return false;
        }

        var matches = snapshot.Find(table.ObjectName, table.SchemaName);

        if (matches.Count == 0)
        {
            return false;
        }

        owner = matches[0];
        return true;
    }

    private static SqlObjectLocation? BuildColumnLocation(
        SqlIdentifierReference reference,
        SqlObjectInfo owner,
        SqlObjectDetail? detail)
    {
        if (detail is null)
        {
            // 明細還沒回來時仍回報物件，呼叫端可以顯示載入中的內容。
            return new SqlObjectLocation(reference, owner);
        }

        foreach (var column in detail.Columns)
        {
            if (string.Equals(column.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new SqlObjectLocation(reference, owner, column);
            }
        }

        // 限定詞確實是資料來源，只是沒有這個欄位；不要退回去猜同名的資料庫物件。
        return null;
    }

    /// <summary>
    /// 把識別字解析成資料庫物件。
    /// </summary>
    /// <remarks>
    /// 沒有限定詞的識別字可能是敘述裡的別名，這時要換成別名指向的資料表。
    /// 別名優先於同名物件：<c>FROM Orders AS Publisher</c> 之後的 <c>Publisher</c> 是 Orders。
    /// </remarks>
    private static IReadOnlyList<SqlObjectInfo> ResolveObject(
        SqlDatabaseSnapshot snapshot,
        SqlStatementScope scope,
        SqlIdentifierReference reference)
    {
        if (reference.Qualifier is null &&
            scope.TryResolve(reference.Name, out var aliased) &&
            !aliased.IsDerived)
        {
            var byAlias = snapshot.Find(aliased.ObjectName, aliased.SchemaName);

            if (byAlias.Count > 0)
            {
                return byAlias;
            }
        }

        var matches = snapshot.Find(reference.Name, reference.Qualifier);

        // 限定詞可能是別的資料庫或找不到的結構描述，退回只用名稱比對。
        return matches.Count == 0 && reference.Qualifier is not null
            ? snapshot.Find(reference.Name)
            : matches;
    }
}
