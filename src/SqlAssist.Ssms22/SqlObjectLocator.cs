using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22;

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
/// 滑鼠停留提示與「複製物件結構」共用同一套判斷，否則兩者對同一個位置
/// 會給出不同答案。
/// </remarks>
internal static class SqlObjectLocator
{
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

        if (snapshot is null)
        {
            return null;
        }

        var scope = SqlScopeAnalyzer.Analyze(text, position);

        // 限定詞指向敘述中的資料來源時，游標停的是欄位而不是物件。
        if (reference.Qualifier is not null &&
            scope.TryResolve(reference.Qualifier, out var owner) &&
            !owner.IsDerived)
        {
            return await LocateColumnAsync(metadataService, snapshot, owner, reference, cancellationToken)
                .ConfigureAwait(false);
        }

        var matches = ResolveObject(snapshot, scope, reference);

        return matches.Count == 0 ? null : new SqlObjectLocation(reference, matches[0]);
    }

    private static async Task<SqlObjectLocation?> LocateColumnAsync(
        SqlMetadataService metadataService,
        SqlDatabaseSnapshot snapshot,
        SqlTableReference owner,
        SqlIdentifierReference reference,
        CancellationToken cancellationToken)
    {
        var matches = snapshot.Find(owner.ObjectName, owner.SchemaName);

        if (matches.Count == 0)
        {
            return null;
        }

        var detail = await metadataService
            .GetDetailAsync(matches[0], cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            // 明細還沒回來時仍回報物件，呼叫端可以顯示載入中的內容。
            return new SqlObjectLocation(reference, matches[0]);
        }

        foreach (var column in detail.Columns)
        {
            if (string.Equals(column.Name, reference.Name, System.StringComparison.OrdinalIgnoreCase))
            {
                return new SqlObjectLocation(reference, matches[0], column);
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
    private static System.Collections.Generic.IReadOnlyList<SqlObjectInfo> ResolveObject(
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
