using System;

namespace SqlAssist.Core.SqlMemory;

/// <summary>實作必須為不可變、可跨執行緒讀取的快照；熱路徑只讀 Length，不展開全文。</summary>
public interface ISqlTextSnapshot
{
    int Length { get; }
    string GetText();
}

public sealed class SqlTextSnapshot : ISqlTextSnapshot
{
    private readonly string _text;
    public SqlTextSnapshot(string text) => _text = text ?? throw new ArgumentNullException(nameof(text));
    public int Length => _text.Length;
    public string GetText() => _text;
}

/// <summary>Sequence 在同一 Session 嚴格遞增；選取執行時仍須攜帶完整文件快照。</summary>
public sealed class SqlCapture
{
    public SqlCapture(Guid captureId, SqlDocument document, SqlSession session,
        long sequence, DateTimeOffset capturedAt, SqlCaptureKind kind, ISqlTextSnapshot documentText,
        SqlConnectionLabel? connection = null, ISqlTextSnapshot? selectedText = null)
    {
        if (captureId == Guid.Empty) throw new ArgumentException("擷取識別碼不可為空。", nameof(captureId));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (document.DocumentId == Guid.Empty || session.SessionId == Guid.Empty || session.DocumentId != document.DocumentId)
            throw new ArgumentException("文件與 Session 識別碼不一致。", nameof(session));
        if (session.ClosedAt != null) throw new ArgumentException("擷取來源必須是開啟的 Session。", nameof(session));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!Enum.IsDefined(typeof(SqlCaptureKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (selectedText != null && kind != SqlCaptureKind.BeforeExecute)
            throw new ArgumentException("只有執行事件可攜帶選取文字。", nameof(selectedText));
        CaptureId = captureId;
        Sequence = sequence;
        CapturedAt = capturedAt.ToUniversalTime();
        Kind = kind;
        DocumentText = documentText ?? throw new ArgumentNullException(nameof(documentText));
        Connection = connection;
        SelectedText = selectedText;
        if (documentText.Length < 0 || (selectedText != null && selectedText.Length < 0))
            throw new ArgumentException("快照長度不可為負數。", nameof(documentText));
        EstimatedTextBytes = 2L * (documentText.Length + (long)(selectedText?.Length ?? 0));
    }

    public Guid CaptureId { get; }
    public SqlDocument Document { get; }
    public SqlSession Session { get; }
    public long Sequence { get; }
    public DateTimeOffset CapturedAt { get; }
    public SqlCaptureKind Kind { get; }
    public ISqlTextSnapshot DocumentText { get; }
    public SqlConnectionLabel? Connection { get; }
    public ISqlTextSnapshot? SelectedText { get; }
    public long EstimatedTextBytes { get; }
}
