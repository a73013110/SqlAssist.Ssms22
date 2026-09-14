using System;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.QueryMemory;

/// <summary>
/// 一個查詢視窗的擷取來源：身分、停止輸入的去彈跳、執行與關閉。
/// </summary>
/// <remarks>
/// 熱路徑只做三件事：一次欄位讀取、記下最後編輯時間、重排去彈跳計時器。
/// SQL 全文到背景才展開——<see cref="ITextSnapshot"/> 不可變，跨執行緒讀是安全的，
/// 在按鍵路徑上 <c>GetText()</c> 則是每打一個字複製一整份查詢。
///
/// 每一個 <see cref="IWpfTextView"/> 是一個 Session；同一個檔案在不同視窗、不同次
/// 啟動都是不同 Session，但共用同一個 DocumentId，歷程才串得起來。檔名與路徑在視窗開著時
/// 會變（第一次存檔、另存新檔），所以每次擷取前重讀，規則在 <see cref="QueryDocumentIdentity"/>。
/// </remarks>
internal sealed class QueryMemorySessionTracker
{
    private readonly IWpfTextView _textView;
    private readonly IServiceProvider _serviceProvider;
    private readonly QueryDocumentIdentity _identity;
    private readonly DispatcherTimer _idle;
    private int _closed;

    private QueryMemorySessionTracker(IWpfTextView textView, IServiceProvider serviceProvider, QueryDocumentIdentity identity)
    {
        _textView = textView;
        _serviceProvider = serviceProvider;
        _identity = identity;
        _idle = new DispatcherTimer(DispatcherPriority.Background, textView.VisualElement.Dispatcher);
        _idle.Tick += OnIdle;
    }

    private static QueryMemoryRuntime Runtime => QueryMemoryHost.Runtime;

    /// <summary>建立編輯器時接上；設定是關的也照接，開關由 <see cref="QueryMemoryRuntime"/> 當場回答。</summary>
    public static void Attach(IWpfTextView textView, IServiceProvider serviceProvider)
    {
        if (textView is null || serviceProvider is null) return;

        var buffer = textView.TextBuffer;
        var tracker = new QueryMemorySessionTracker(textView, serviceProvider,
            new QueryDocumentIdentity(ActiveSqlEditor.GetDocumentName(buffer), FilePath(buffer), DateTimeOffset.UtcNow));

        textView.Properties[typeof(QueryMemorySessionTracker)] = tracker;
        buffer.Changed += tracker.OnBufferChanged;
        textView.Closed += tracker.OnClosed;
        QueryMemoryExecuteCommand.EnsureResolved(serviceProvider);
    }

    /// <summary>殼層送出「執行查詢」時呼叫；擷取的是送出前的內容，與執行結果無關。</summary>
    public static void NoteExecute(IWpfTextView textView)
    {
        if (textView is not null &&
            textView.Properties.TryGetProperty(typeof(QueryMemorySessionTracker), out QueryMemorySessionTracker tracker))
        {
            tracker.CaptureExecute();
        }
    }

    /// <remarks>
    /// 每按一次鍵都會進來一次。先問開關再動計時器：關掉查詢記憶之後，這裡就只剩
    /// 一次欄位讀取。
    /// </remarks>
    private void OnBufferChanged(object sender, TextContentChangedEventArgs eventArgs)
    {
        if (!Runtime.IsCapturing) return;

        Runtime.NoteEdit();
        _idle.Stop();
        _idle.Interval = Runtime.IdleDebounce;
        _idle.Start();
    }

    private void OnIdle(object sender, EventArgs eventArgs)
    {
        _idle.Stop();
        // 不走 Guard 之外的路：這是派送佇列上的工作，沒有人接它的結果。
        SqlAssistPlatformGuard.Run("記下查詢草稿", () => Capture(QueryCaptureKind.DraftIdle, selection: null));
    }

    private void CaptureExecute() => SqlAssistPlatformGuard.Run("記下執行的查詢", () =>
    {
        // 選取執行是執行專用版本，不會變成文件 head，也不覆蓋整份文件的未存檔草稿。
        Capture(QueryCaptureKind.BeforeExecute, SelectedText(_textView.Selection));
    });

    private void OnClosed(object sender, EventArgs eventArgs)
    {
        _textView.TextBuffer.Changed -= OnBufferChanged;
        _textView.Closed -= OnClosed;
        _idle.Stop();
        _idle.Tick -= OnIdle;

        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        SqlAssistPlatformGuard.Run("記下查詢視窗關閉", () =>
        {
            // 正式關閉要在同一筆交易裡保存最終版本並刪掉未存檔草稿，所以即使關掉
            // 草稿擷取也照送——版本引擎自己決定要不要留內容。
            Capture(QueryCaptureKind.EditorClosed, selection: null);
            QueryMemoryConnections.Forget(Moniker());
        });
    }

    private void Capture(QueryCaptureKind kind, IQueryTextSnapshot? selection)
    {
        if (!Runtime.IsCapturing) return;

        var now = DateTimeOffset.UtcNow;
        var text = new SnapshotText(_textView.TextBuffer.CurrentSnapshot);
        var connection = QueryMemoryConnections.Get(Moniker());
        var buffer = _textView.TextBuffer;
        if (_identity.Observe(ActiveSqlEditor.GetDocumentName(buffer), FilePath(buffer), now) is { } handover)
        {
            // 存檔或另存之後換到新路徑的文件；舊 Session 以當下內容正式關閉，它的未存檔草稿跟著清掉。
            Runtime.TryEnqueue(new QueryMemoryCapture(Guid.NewGuid(), handover.Document, handover.Session,
                handover.Sequence, now, QueryCaptureKind.EditorClosed, text, connection));
        }

        Runtime.TryEnqueue(new QueryMemoryCapture(Guid.NewGuid(), _identity.Document, _identity.Session,
            _identity.NextSequence(), now, kind, text, connection, selection));
    }

    /// <summary>
    /// 殼層實際送出的文字：每個選取範圍依文件順序、以文件自己的換行串起來。
    /// 方塊選取不能取第一個起點到最後一個終點，中間欄外的文字並沒有被執行。
    /// </summary>
    private static IQueryTextSnapshot? SelectedText(ITextSelection selection)
    {
        if (selection is null || selection.IsEmpty) return null;

        var spans = selection.SelectedSpans.OrderBy(span => span.Start.Position).ToArray();
        if (spans.Length == 0) return null;
        return QuerySelectionText.Combine(spans.Select(span => (IQueryTextSnapshot)new SpanText(span)).ToArray(),
            SnapshotNewLine.Resolve(spans[0].Snapshot, spans[0].Start.Position));
    }

    private string? Moniker() => SqlAssistPlatformGuard.Probe("取得查詢視窗識別",
        () => SqlCompletionServices.GetMetadataService(_textView, _serviceProvider).EditorMoniker, fallback: null);

    private static string? FilePath(ITextBuffer buffer) => SqlAssistPlatformGuard.Probe("取得查詢檔案路徑",
        () => buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
            ? document.FilePath
            : null, fallback: null);

    /// <summary>整份文件的快照。不可變，背景展開全文才安全也才便宜。</summary>
    private sealed class SnapshotText : IQueryTextSnapshot
    {
        private readonly ITextSnapshot _snapshot;

        public SnapshotText(ITextSnapshot snapshot) => _snapshot = snapshot;

        public int Length => _snapshot.Length;

        public string GetText() => _snapshot.GetText();
    }

    /// <summary>選取範圍的快照；綁在取得當下的那一份 snapshot 上，之後的編輯不影響它。</summary>
    private sealed class SpanText : IQueryTextSnapshot
    {
        private readonly SnapshotSpan _span;

        public SpanText(SnapshotSpan span) => _span = span;

        public int Length => _span.Length;

        public string GetText() => _span.GetText();
    }
}
