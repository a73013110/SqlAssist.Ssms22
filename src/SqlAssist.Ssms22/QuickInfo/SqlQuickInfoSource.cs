using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core;

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

        var location = await SqlObjectLocator
            .LocateAsync(_metadataService, snapshot.GetText(), point.Position, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return null;
        }

        var applicableSpan = snapshot.CreateTrackingSpan(
            new Span(location.Reference.Start, location.Reference.Length),
            SpanTrackingMode.EdgeInclusive);

        if (location.Column is { } column)
        {
            SqlAssistDiagnostics.Write($"已顯示欄位提示：{location.Object.QualifiedName}.{column.Name}");
            return new QuickInfoItem(
                applicableSpan,
                SqlQuickInfoContentBuilder.BuildColumn(location.Object, column));
        }

        var detail = await _metadataService
            .GetDetailAsync(location.Object, cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            return new QuickInfoItem(
                applicableSpan,
                SqlQuickInfoContentBuilder.BuildLoading(location.Object));
        }

        SqlAssistDiagnostics.Write($"已顯示物件提示：{location.Object.QualifiedName}");
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
