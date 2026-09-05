using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Settings;

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
    /// <summary>整份文字與上一次的語法分析，依文字快照快取，不保存中繼資料結果。</summary>
    /// <remarks>
    /// 一次滑鼠停留會產生數個 session，滑鼠在同一個字上輕微移動也會重來一次，
    /// 而 <see cref="SqlAssist.Core.Parsing.SqlScopeAnalyzer"/> 每一次都要對整份文字
    /// 做詞法分析。以單一不可變物件整份換掉，讀取端不必擔心欄位之間彼此不同步。
    /// </remarks>
    private sealed class ParsedIdentifier
    {
        public ParsedIdentifier(ITextSnapshot snapshot, string text, SqlObjectLookup lookup)
        {
            Snapshot = snapshot;
            Text = text;
            Lookup = lookup;
        }

        public ITextSnapshot Snapshot { get; }

        public string Text { get; }

        public SqlObjectLookup Lookup { get; }
    }

    private readonly ITextBuffer _textBuffer;
    private readonly IServiceProvider _serviceProvider;
    private ParsedIdentifier? _parsed;
    private bool _disposed;

    public SqlQuickInfoSource(ITextBuffer textBuffer, IServiceProvider serviceProvider)
    {
        _textBuffer = textBuffer;
        _serviceProvider = serviceProvider;
    }

    public Task<QuickInfoItem?> GetQuickInfoItemAsync(
        IAsyncQuickInfoSession session,
        CancellationToken cancellationToken)
    {
        // 提示視窗失敗絕不可以影響編輯；記錄後安靜地什麼都不顯示。
        return SqlAssistPlatformGuard.RunAsync<QuickInfoItem?>(
            "物件提示產生",
            () => GetQuickInfoCoreAsync(session, cancellationToken),
            fallback: null);
    }

    private async Task<QuickInfoItem?> GetQuickInfoCoreAsync(
        IAsyncQuickInfoSession session,
        CancellationToken cancellationToken)
    {
        var settings = SqlAssistSettingsStore.Current;

        if (_disposed || !settings.Enabled || !settings.HoverEnabled)
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

        // 提示只給一眼看得完的份量，看不完的那一半交給浮動預覽。
        var openStructure = CreateOpenStructureAction(
            textView,
            metadataService,
            applicableSpan,
            location.Object);

        if (location.Column is { } column)
        {
            SqlAssistDiagnostics.Write($"已顯示欄位提示：{location.Object.QualifiedName}.{column.Name}");
            return new QuickInfoItem(
                applicableSpan,
                SqlQuickInfoContentBuilder.BuildColumn(location.Object, column, openStructure));
        }

        var detail = metadataService.PeekDetail(location.Object);

        if (detail is null)
        {
            // 快取沒有就這一輪只顯示標題，背景補上之後下一次停留就有內容。
            metadataService.WarmDetail(location.Object);
            return new QuickInfoItem(
                applicableSpan,
                SqlQuickInfoContentBuilder.BuildLoading(location.Object, openStructure));
        }

        SqlAssistDiagnostics.Write($"已顯示物件提示：{location.Object.QualifiedName}");
        return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.Build(detail, openStructure));
    }

    /// <summary>
    /// 「開啟完整結構」連結要執行的動作；由編輯器在使用者點擊時呼叫。
    /// </summary>
    /// <remarks>
    /// 開的是建議清單用的同一個浮動預覽，錨在同一個識別字上。
    /// 兩條入口共用一份視窗與一份載入邏輯，行為與外觀不會有兩套。
    /// </remarks>
    private Action CreateOpenStructureAction(
        ITextView textView,
        SqlMetadataService metadataService,
        ITrackingSpan anchor,
        SqlObjectInfo objectInfo)
    {
        return () =>
        {
            if (SqlStructurePreview.GetOrCreate(textView, _serviceProvider) is { } preview)
            {
                preview.ShowAt(anchor, objectInfo, metadataService);
            }
        };
    }

    /// <summary>只重用語法分析；清快取與背景載入不會改變 SQL 文字，物件與欄位必須重新比對。</summary>
    private SqlObjectLocation? Resolve(SqlMetadataService metadataService, ITextSnapshot snapshot, int position)
    {
        var parsed = Volatile.Read(ref _parsed);
        var text = parsed is not null && ReferenceEquals(parsed.Snapshot, snapshot)
            ? parsed.Text
            : snapshot.GetText();

        var reference = SqlIdentifierScanner.FindAt(text, position);

        if (reference is null)
        {
            return null;
        }

        if (parsed is not null &&
            ReferenceEquals(parsed.Snapshot, snapshot) &&
            reference.Start == parsed.Lookup.Reference.Start &&
            reference.End == parsed.Lookup.Reference.End)
        {
            return SqlObjectLocator.LocateCached(metadataService, parsed.Lookup);
        }

        var lookup = SqlObjectLookup.Create(text, position);
        if (lookup is null)
        {
            return null;
        }

        Volatile.Write(ref _parsed, new ParsedIdentifier(snapshot, text, lookup));
        return SqlObjectLocator.LocateCached(metadataService, lookup);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 中繼資料服務的所有權在 TextView，這裡只放掉自己的快取。
        _disposed = true;
        Volatile.Write(ref _parsed, null);
    }
}
