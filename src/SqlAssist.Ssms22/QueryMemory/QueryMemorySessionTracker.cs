using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.QueryMemory;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Editor;

namespace SqlAssist.Ssms22.QueryMemory;

/// <summary>
/// 一個查詢視窗的擷取來源：Session 身分、停止輸入的去彈跳、執行與關閉。
/// </summary>
/// <remarks>
/// 熱路徑只做三件事：一次靜態旗標讀取、記下最後編輯時間、重排去彈跳計時器。
/// SQL 全文到背景才展開——<see cref="ITextSnapshot"/> 不可變，跨執行緒讀是安全的，
/// 在按鍵路徑上 <c>GetText()</c> 則是每打一個字複製一整份查詢。
///
/// 每一個 <see cref="IWpfTextView"/> 是一個 Session；同一個檔案在不同視窗、不同次
/// 啟動都是不同 Session，但共用同一個 DocumentId，歷程才串得起來。
/// </remarks>
internal sealed class QueryMemorySessionTracker
{
    private readonly IWpfTextView _textView;
    private readonly IServiceProvider _serviceProvider;
    private readonly QueryDocument _document;
    private readonly QuerySession _session;
    private readonly DispatcherTimer _idle;
    private long _sequence;
    private int _closed;

    private QueryMemorySessionTracker(IWpfTextView textView, IServiceProvider serviceProvider,
        QueryDocument document, QuerySession session)
    {
        _textView = textView;
        _serviceProvider = serviceProvider;
        _document = document;
        _session = session;
        _idle = new DispatcherTimer(DispatcherPriority.Background, textView.VisualElement.Dispatcher);
        _idle.Tick += OnIdle;
    }

    /// <summary>建立編輯器時接上；設定是關的也照接，開關由 <see cref="QueryMemoryHost"/> 當場回答。</summary>
    public static void Attach(IWpfTextView textView, IServiceProvider serviceProvider)
    {
        if (textView is null || serviceProvider is null) return;

        var buffer = textView.TextBuffer;
        var filePath = FilePath(buffer);
        var document = new QueryDocument(DocumentId(filePath), ActiveSqlEditor.GetDocumentName(buffer), filePath);
        var tracker = new QueryMemorySessionTracker(textView, serviceProvider, document,
            new QuerySession(Guid.NewGuid(), document.DocumentId, DateTimeOffset.UtcNow));

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
    /// 一次靜態欄位讀取。
    /// </remarks>
    private void OnBufferChanged(object sender, TextContentChangedEventArgs eventArgs)
    {
        if (!QueryMemoryHost.IsCapturing) return;

        QueryMemoryHost.NoteEdit();
        _idle.Stop();
        _idle.Interval = QueryMemoryHost.IdleDebounce;
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
        var selection = _textView.Selection;
        // 選取執行是執行專用版本，不會變成文件 head，也不覆蓋整份文件的未存檔草稿。
        var selected = selection is { IsEmpty: false }
            ? new SpanText(selection.SelectedSpans.Count == 1
                ? selection.SelectedSpans[0]
                : new SnapshotSpan(selection.Start.Position, selection.End.Position))
            : null;
        Capture(QueryCaptureKind.BeforeExecute, selected);
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
        if (!QueryMemoryHost.IsCapturing) return;

        var capture = new QueryMemoryCapture(Guid.NewGuid(), _document, _session,
            Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, kind,
            new SnapshotText(_textView.TextBuffer.CurrentSnapshot), QueryMemoryConnections.Get(Moniker()), selection);

        QueryMemoryHost.TryEnqueue(capture);
    }

    private string? Moniker() => SqlAssistPlatformGuard.Probe("取得查詢視窗識別",
        () => SqlCompletionServices.GetMetadataService(_textView, _serviceProvider).EditorMoniker, fallback: null);

    private static string? FilePath(ITextBuffer buffer) => SqlAssistPlatformGuard.Probe("取得查詢檔案路徑",
        () => buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
            ? document.FilePath
            : null, fallback: null);

    /// <summary>
    /// 同一個檔案跨視窗、跨重啟都是同一份文件。
    /// </summary>
    /// <remarks>
    /// 由完整路徑推出來而不是存一張對照表：對照表活不過重新啟動，而歷程要串得起來的
    /// 正是「上週那個檔案」。還沒存檔的查詢沒有路徑可推，每個視窗各自成一份文件——
    /// 拿標題（SQLQuery1.sql）當身分的話，不同時候的兩個新查詢會被併成同一份。
    /// </remarks>
    private static Guid DocumentId(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Guid.NewGuid();

        var normalized = Path.GetFullPath(filePath!).ToLowerInvariant();
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.Unicode.GetBytes(normalized));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

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
