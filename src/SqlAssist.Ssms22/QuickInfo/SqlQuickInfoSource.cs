using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata;

namespace SqlAssist.Ssms22.QuickInfo;

/// <summary>
/// 滑鼠停留在資料庫物件上時，顯示該物件的結構。
/// </summary>
internal sealed class SqlQuickInfoSource : IAsyncQuickInfoSource
{
    private readonly ITextBuffer _textBuffer;
    private readonly SqlMetadataService _metadataService;
    private bool _disposed;

    public SqlQuickInfoSource(ITextBuffer textBuffer, IServiceProvider serviceProvider)
    {
        _textBuffer = textBuffer;
        _metadataService = new SqlMetadataService(serviceProvider);
    }

    public async Task<QuickInfoItem?> GetQuickInfoItemAsync(
        IAsyncQuickInfoSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetQuickInfoCoreAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            // 提示視窗失敗絕不可以影響編輯；記錄後安靜地什麼都不顯示。
            SqlAssistDiagnostics.WriteAlways($"物件提示產生失敗：{exception.Message}");
            return null;
        }
    }

    private async Task<QuickInfoItem?> GetQuickInfoCoreAsync(
        IAsyncQuickInfoSession session,
        CancellationToken cancellationToken)
    {
        var settings = SettingsService.Default.GetSnapshot();

        if (_disposed || !settings.Enabled || !settings.Features.ObjectHover)
        {
            return null;
        }

        var snapshot = _textBuffer.CurrentSnapshot;
        var triggerPoint = session.GetTriggerPoint(snapshot);

        if (triggerPoint is not { } point)
        {
            return null;
        }

        var text = snapshot.GetText();
        var reference = SqlIdentifierScanner.FindAt(text, point.Position);

        if (reference is null)
        {
            return null;
        }

        var databaseSnapshot = await _metadataService
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        if (databaseSnapshot is null)
        {
            return null;
        }

        var applicableSpan = snapshot.CreateTrackingSpan(
            new Span(reference.Start, reference.Length),
            SpanTrackingMode.EdgeInclusive);
        var scope = SqlScopeAnalyzer.Analyze(text, point.Position);

        // 限定詞指向敘述中的資料來源時，游標停的是欄位而不是物件。
        if (reference.Qualifier is not null &&
            scope.TryResolve(reference.Qualifier, out var owner) &&
            !owner.IsDerived)
        {
            return await BuildColumnItemAsync(databaseSnapshot, owner, reference, applicableSpan, cancellationToken)
                .ConfigureAwait(false);
        }

        var matches = ResolveObject(databaseSnapshot, scope, reference);

        if (matches.Count == 0)
        {
            return null;
        }

        var objectInfo = matches[0];

        var detail = await _metadataService
            .GetDetailAsync(objectInfo, cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.BuildLoading(objectInfo));
        }

        SqlAssistDiagnostics.Write($"已顯示物件提示：{objectInfo.QualifiedName}");
        return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.Build(detail));
    }

    /// <summary>
    /// 把識別字解析成資料庫物件。
    /// </summary>
    /// <remarks>
    /// 沒有限定詞的識別字可能是敘述裡的別名，這時要換成別名指向的資料表。
    /// 別名優先於同名物件：<c>FROM Orders AS Publisher</c> 之後的 <c>Publisher</c> 是 Orders。
    /// </remarks>
    private static IReadOnlyList<SqlObjectInfo> ResolveObject(
        SqlDatabaseSnapshot databaseSnapshot,
        SqlStatementScope scope,
        SqlIdentifierReference reference)
    {
        if (reference.Qualifier is null &&
            scope.TryResolve(reference.Name, out var aliased) &&
            !aliased.IsDerived)
        {
            var byAlias = databaseSnapshot.Find(aliased.ObjectName, aliased.SchemaName);

            if (byAlias.Count > 0)
            {
                return byAlias;
            }
        }

        var matches = databaseSnapshot.Find(reference.Name, reference.Qualifier);

        // 限定詞可能是別的資料庫或找不到的結構描述，退回只用名稱比對。
        if (matches.Count == 0 && reference.Qualifier is not null)
        {
            matches = databaseSnapshot.Find(reference.Name);
        }

        return matches;
    }

    private async Task<QuickInfoItem?> BuildColumnItemAsync(
        SqlDatabaseSnapshot databaseSnapshot,
        SqlTableReference owner,
        SqlIdentifierReference reference,
        ITrackingSpan applicableSpan,
        CancellationToken cancellationToken)
    {
        var ownerMatches = databaseSnapshot.Find(owner.ObjectName, owner.SchemaName);

        if (ownerMatches.Count == 0)
        {
            return null;
        }

        var detail = await _metadataService
            .GetDetailAsync(ownerMatches[0], cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.BuildLoading(ownerMatches[0]));
        }

        foreach (var column in detail.Columns)
        {
            if (!string.Equals(column.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SqlAssistDiagnostics.Write($"已顯示欄位提示：{ownerMatches[0].QualifiedName}.{column.Name}");
            return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.BuildColumn(ownerMatches[0], column));
        }

        // 限定詞確實是資料來源，只是沒有這個欄位；不要退回去猜同名的資料庫物件。
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _metadataService.Dispose();
    }
}
