using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core;
using SqlAssist.Ssms22.Completion;

namespace SqlAssist.Ssms22.QuickInfo;

/// <summary>
/// 滑鼠停留在資料庫物件上時，顯示該物件的結構。
/// </summary>
/// <remarks>
/// 這條路徑在滑鼠移動的軌跡上，成本必須接近零：中繼資料只讀快取，
/// 連線只用已經解析好的目錄，同一個識別字重複停留也只解析一次。
/// </remarks>
internal sealed class SqlQuickInfoSource : IAsyncQuickInfoSource
{
    /// <summary>整份文字與上一次的解析結果，依快照快取。</summary>
    /// <remarks>
    /// 一次滑鼠停留會產生數個 session，滑鼠在同一個字上輕微移動也會重來一次，
    /// 而 <see cref="SqlAssist.Core.Parsing.SqlScopeAnalyzer"/> 每一次都要對整份文字
    /// 做詞法分析。以單一不可變物件整份換掉，讀取端不必擔心欄位之間彼此不同步。
    /// </remarks>
    private sealed class ResolvedIdentifier
    {
        public ResolvedIdentifier(ITextSnapshot snapshot, string text, int start, int end, SqlObjectLocation? location)
        {
            Snapshot = snapshot;
            Text = text;
            Start = start;
            End = end;
            Location = location;
        }

        public ITextSnapshot Snapshot { get; }

        public string Text { get; }

        public int Start { get; }

        public int End { get; }

        /// <summary>解析結果；掃到識別字但不是資料庫物件時為 null，這個「沒有」同樣值得記住。</summary>
        public SqlObjectLocation? Location { get; }
    }

    private readonly ITextBuffer _textBuffer;
    private readonly IServiceProvider _serviceProvider;
    private ResolvedIdentifier? _resolved;
    private bool _disposed;

    public SqlQuickInfoSource(ITextBuffer textBuffer, IServiceProvider serviceProvider)
    {
        _textBuffer = textBuffer;
        _serviceProvider = serviceProvider;
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

        var textView = session.TextView;

        if (textView is null)
        {
            return null;
        }

        var snapshot = _textBuffer.CurrentSnapshot;
        var triggerPoint = session.GetTriggerPoint(snapshot);

        if (triggerPoint is not { } point)
        {
            return null;
        }

        // 建議清單、ALTER 展開與這裡看的是同一份服務：連線解析與三層快取都只做一次。
        // 先前這裡各自 new 一份，等於在滑鼠移動的軌跡上另外開一條會問 SSMS 連線的支線，
        // 而那個呼叫有 UI 執行緒相依性，忙的時候會直接反映成打字延遲。
        var metadataService = SqlCompletionServices.GetMetadataService(textView, _serviceProvider);

        // 詞法分析要掃過整份文字，不留在呼叫端的執行緒上。
        var location = await Task
            .Run(() => Resolve(metadataService, snapshot, point.Position), cancellationToken)
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

        var detail = metadataService.PeekDetail(location.Object);

        if (detail is null)
        {
            // 快取沒有就這一輪只顯示標題，背景補上之後下一次停留就有內容。
            metadataService.WarmDetail(location.Object);
            return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.BuildLoading(location.Object));
        }

        SqlAssistDiagnostics.Write($"已顯示物件提示：{location.Object.QualifiedName}");
        return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.Build(detail));
    }

    /// <summary>解析游標處的識別字；快照沒變且位置仍落在上一次的識別字範圍內就沿用結果。</summary>
    private SqlObjectLocation? Resolve(SqlMetadataService metadataService, ITextSnapshot snapshot, int position)
    {
        var resolved = Volatile.Read(ref _resolved);
        var text = resolved is not null && ReferenceEquals(resolved.Snapshot, snapshot)
            ? resolved.Text
            : snapshot.GetText();

        var reference = SqlIdentifierScanner.FindAt(text, position);

        if (reference is null)
        {
            return null;
        }

        if (resolved is not null &&
            ReferenceEquals(resolved.Snapshot, snapshot) &&
            reference.Start == resolved.Start &&
            reference.End == resolved.End)
        {
            return resolved.Location;
        }

        var location = SqlObjectLocator.LocateCached(metadataService, text, position);
        Volatile.Write(
            ref _resolved,
            new ResolvedIdentifier(snapshot, text, reference.Start, reference.End, location));

        return location;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 中繼資料服務的所有權在 TextView，這裡只放掉自己的快取。
        _disposed = true;
        Volatile.Write(ref _resolved, null);
    }
}
