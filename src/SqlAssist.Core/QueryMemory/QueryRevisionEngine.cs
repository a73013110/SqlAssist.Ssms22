using System;
using System.Collections.Generic;

namespace SqlAssist.Core.QueryMemory;

/// <summary>無快取、無 I/O；只在背景將快照轉成可原子提交的寫入計畫。</summary>
public sealed class QueryRevisionEngine
{
    public QueryMemoryWrite? Prepare(QueryMemoryCapture capture, QuerySessionState? previous, QueryMemoryPolicy policy)
    {
        if (capture == null) throw new ArgumentNullException(nameof(capture));
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        if (previous != null)
        {
            if (previous.Session.SessionId != capture.Session.SessionId ||
                previous.Session.DocumentId != capture.Document.DocumentId)
                throw new ArgumentException("Session 狀態不屬於本次擷取。", nameof(previous));
            // 序號而非時鐘決定先後；延遲抵達的 idle 不得覆蓋關閉或較新的 recovery。
            if (previous.Session.ClosedAt != null || capture.Sequence <= previous.LastSequence) return null;
        }

        var executing = capture.Kind == QueryCaptureKind.BeforeExecute;
        var explicitSnapshot = capture.Kind == QueryCaptureKind.ManualSnapshot || capture.Kind == QueryCaptureKind.Recovery;
        if (executing && !policy.CaptureExecutedSql) return null;
        if (!executing && !explicitSnapshot && !policy.CaptureUnexecutedDrafts &&
            !(capture.Kind == QueryCaptureKind.EditorClosed && previous != null)) return null;

        var contents = new List<QueryContent>();
        var revisions = new List<QueryRevision>();
        var latest = previous?.LatestRevision;
        var latestExecution = previous?.LatestExecutionRevision;
        QueryRecoverySnapshot? recovery = null;
        QueryExecutionEvent? execution = null;
        QueryContent? documentContent = null;

        // 關閉 draft 擷取後，選取執行不能偷偷保存未執行的整份文件。
        var includeDocument = policy.CaptureUnexecutedDrafts || explicitSnapshot ||
            (executing && capture.SelectedText == null);
        if (includeDocument)
        {
            documentContent = Materialize(capture.DocumentText);
            var changed = latest?.ContentId != documentContent.ContentId;
            var baseline = latest?.CreatedAt;
            var autoDue = policy.AutoRevisionEnabled &&
                (!baseline.HasValue || capture.CapturedAt - baseline.Value >= policy.AutoRevisionInterval);
            var create = explicitSnapshot || (changed && (capture.Kind != QueryCaptureKind.DraftIdle || autoDue));
            if (create)
            {
                latest = new QueryRevision(Guid.NewGuid(), latest?.RevisionId, documentContent.ContentId,
                    capture.Session.SessionId, capture.CapturedAt, Reason(capture.Kind), capture.Connection);
                revisions.Add(latest);
                contents.Add(documentContent);
            }
            if (policy.RecoveryEnabled && policy.CaptureUnexecutedDrafts && capture.Kind != QueryCaptureKind.EditorClosed)
            {
                recovery = new QueryRecoverySnapshot(capture.Session.SessionId, documentContent.ContentId,
                    capture.Sequence, capture.CapturedAt, capture.Connection);
                if (contents.Count == 0) contents.Add(documentContent);
            }
        }

        if (executing)
        {
            QueryRevision executionRevision;
            if (capture.SelectedText != null)
            {
                var selected = Materialize(capture.SelectedText);
                if (latest != null && latest.ContentId == selected.ContentId)
                    executionRevision = latest;
                else if (latestExecution != null && latestExecution.ContentId == selected.ContentId)
                    executionRevision = latestExecution;
                else
                {
                    // 選取 SQL 是執行專用版本，絕不成為文件 head 或 recovery。
                    executionRevision = new QueryRevision(Guid.NewGuid(), latest?.RevisionId, selected.ContentId,
                        capture.Session.SessionId, capture.CapturedAt, QueryRevisionReason.BeforeExecute,
                        capture.Connection, IsExecutionSelection: true);
                    revisions.Add(executionRevision);
                    if (documentContent?.ContentId != selected.ContentId || contents.Count == 0) contents.Add(selected);
                }
            }
            else executionRevision = latest ?? throw new InvalidOperationException("執行缺少文件版本。");
            execution = new QueryExecutionEvent(capture.CaptureId, executionRevision.RevisionId,
                capture.CapturedAt, capture.Connection,
                capture.SelectedText == null ? QueryExecutionScope.Document : QueryExecutionScope.Selection);
            latestExecution = executionRevision;
        }

        var session = previous?.Session ?? capture.Session;
        var closing = capture.Kind == QueryCaptureKind.EditorClosed;
        if (closing) session = session with { ClosedAt = capture.CapturedAt };
        var state = new QuerySessionState(session, checked((previous?.Version ?? 0) + 1),
            capture.Sequence, latest, latestExecution);
        return new QueryMemoryWrite(capture, previous?.Version, state, contents, revisions,
            recovery, closing, execution);
    }

    private static QueryContent Materialize(IQueryTextSnapshot snapshot)
    {
        var text = snapshot.GetText();
        if (text == null || text.Length != snapshot.Length)
            throw new InvalidOperationException("快照文字與宣告長度不一致。");
        return QueryContent.Create(text);
    }

    private static QueryRevisionReason Reason(QueryCaptureKind kind) => kind switch
    {
        QueryCaptureKind.DraftIdle => QueryRevisionReason.AutoCheckpoint,
        QueryCaptureKind.BeforeExecute => QueryRevisionReason.BeforeExecute,
        QueryCaptureKind.EditorClosed => QueryRevisionReason.EditorClosed,
        QueryCaptureKind.ManualSnapshot => QueryRevisionReason.ManualSnapshot,
        QueryCaptureKind.Recovery => QueryRevisionReason.Recovery,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
