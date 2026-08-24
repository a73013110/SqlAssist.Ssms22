using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core;
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

        var reference = SqlIdentifierScanner.FindAt(snapshot.GetText(), point.Position);

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

        // 限定詞可能是結構描述，也可能是資料表別名。先當結構描述解析，
        // 找不到時退回只用名稱比對，別名解析要等語句範圍模型完成才會準確。
        var matches = databaseSnapshot.Find(reference.Name, reference.Qualifier);

        if (matches.Count == 0 && reference.Qualifier is not null)
        {
            matches = databaseSnapshot.Find(reference.Name);
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var objectInfo = matches[0];
        var applicableSpan = snapshot.CreateTrackingSpan(
            new Span(reference.Start, reference.Length),
            SpanTrackingMode.EdgeInclusive);

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
