using System;
using System.Collections.Generic;

namespace SqlAssist.Core.SqlMemory;

/// <summary>無快取、無 I/O；只在背景將快照轉成可原子提交的寫入計畫。</summary>
public sealed class SqlCapturePlanner
{
    public SqlCaptureCommit? Prepare(SqlCapture capture, SqlSessionHead? previous, SqlCapturePolicy policy)
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

        var executing = capture.Kind == SqlCaptureKind.BeforeExecute;
        if (executing && !policy.CaptureExecutedSql) return null;
        if (!executing && !policy.CaptureUnexecutedDrafts &&
            !(capture.Kind == SqlCaptureKind.EditorClosed && previous != null)) return null;

        var contents = new List<SqlContent>();
        var revisions = new List<SqlRevision>();
        var latest = previous?.LatestRevision;
        var latestExecution = previous?.LatestExecutionRevision;
        SqlRecovery? recovery = null;
        SqlExecution? execution = null;
        SqlContent? documentContent = null;

        // 關閉 draft 擷取後，選取執行不能偷偷保存未執行的整份文件。
        var includeDocument = policy.CaptureUnexecutedDrafts || (executing && capture.SelectedText == null);
        var documentText = includeDocument ? Text(capture.DocumentText) : null;
        var selectedText = capture.SelectedText is { } selection ? Text(selection) : null;

        // 空白的 SQL 一列都不留。新開一個查詢視窗又關掉、把整份刪光、對著空白的視窗
        // 按下執行，三種都會走到這裡；記下來的那一列打開是空的，而清單被這種列塞滿之後，
        // 真的要找的那一份反而翻不出來。判斷只有 SqlContent.IsBlank 一份。
        var documentBlank = includeDocument && SqlContent.IsBlank(documentText);

        // 選取了卻全是空白，或整份文件是空白又沒有選取：這一次執行沒有送出任何 SQL。
        // 後者尤其不能沿用上一個版本——那會把一段使用者已經刪掉的 SQL 記成剛剛執行過。
        if (executing && (selectedText != null ? SqlContent.IsBlank(selectedText) : documentBlank)) return null;

        // 空白文件的第一次擷取：這個 Session 連一列都不該出現在清單上。已經有內容的
        // Session 則照常往下走——關閉仍要保存最終版本並刪掉 Recovery，只是不為空白建版本。
        if (documentBlank && previous == null) return null;

        if (includeDocument && !documentBlank)
        {
            documentContent = SqlContent.Create(documentText!);
            var changed = latest?.ContentId != documentContent.ContentId;
            var baseline = latest?.CreatedAt;
            var autoDue = policy.AutoRevisionEnabled &&
                (!baseline.HasValue || capture.CapturedAt - baseline.Value >= policy.AutoRevisionInterval);
            var create = changed && (capture.Kind != SqlCaptureKind.DraftIdle || autoDue);
            if (create)
            {
                latest = new SqlRevision(Guid.NewGuid(), latest?.RevisionId, documentContent.ContentId,
                    capture.Session.SessionId, capture.CapturedAt, Reason(capture.Kind));
                revisions.Add(latest);
                contents.Add(documentContent);
            }
            if (policy.RecoveryEnabled && policy.CaptureUnexecutedDrafts && capture.Kind != SqlCaptureKind.EditorClosed)
            {
                // 沒有新版本、沒有執行、且 Recovery 已經指向同一份內容時，完全不必再寫一次 Recovery／Content。
                var recoveryUnchanged = !executing && !create && previous?.RecoveryContentId == documentContent.ContentId;
                if (!recoveryUnchanged)
                {
                    recovery = new SqlRecovery(capture.Session.SessionId, documentContent.ContentId, capture.CapturedAt);
                    if (contents.Count == 0) contents.Add(documentContent);
                }
            }
        }

        if (executing)
        {
            SqlRevision executionRevision;
            if (selectedText != null)
            {
                var selected = SqlContent.Create(selectedText);
                if (latest != null && latest.ContentId == selected.ContentId)
                    executionRevision = latest;
                else if (latestExecution != null && latestExecution.ContentId == selected.ContentId)
                    executionRevision = latestExecution;
                else
                {
                    // 選取 SQL 是執行專用版本，絕不成為文件 head 或 recovery。
                    executionRevision = new SqlRevision(Guid.NewGuid(), latest?.RevisionId, selected.ContentId,
                        capture.Session.SessionId, capture.CapturedAt, SqlRevisionReason.BeforeExecute,
                        IsExecutionSelection: true);
                    revisions.Add(executionRevision);
                    if (documentContent?.ContentId != selected.ContentId || contents.Count == 0) contents.Add(selected);
                }
            }
            else executionRevision = latest ?? throw new InvalidOperationException("執行缺少文件版本。");
            execution = new SqlExecution(capture.CaptureId, executionRevision.RevisionId, capture.CapturedAt);
            latestExecution = executionRevision;
        }

        var session = previous?.Session ?? capture.Session;
        var closing = capture.Kind == SqlCaptureKind.EditorClosed;
        if (closing) session = session with { ClosedAt = capture.CapturedAt };
        // 沒有產生新 Recovery 寫入時，沿用先前的 ContentId；關閉一律清空。
        var recoveryContentId = closing ? null : recovery?.ContentId ?? previous?.RecoveryContentId;
        var state = new SqlSessionHead(session, checked((previous?.Version ?? 0) + 1),
            capture.Sequence, latest, latestExecution, recoveryContentId);
        return new SqlCaptureCommit(capture, previous?.Version, state, contents, revisions,
            recovery, closing, execution);
    }

    /// <remarks>
    /// 展開全文只在這裡做一次：空白判斷與內容雜湊吃的是同一份字串，各取一次等於
    /// 在背景把一份動輒數 MB 的查詢複製兩遍。
    /// </remarks>
    private static string Text(ISqlTextSnapshot snapshot)
    {
        var text = snapshot.GetText();
        if (text == null || text.Length != snapshot.Length)
            throw new InvalidOperationException("快照文字與宣告長度不一致。");
        return text;
    }

    private static SqlRevisionReason Reason(SqlCaptureKind kind) => kind switch
    {
        SqlCaptureKind.DraftIdle => SqlRevisionReason.AutoCheckpoint,
        SqlCaptureKind.BeforeExecute => SqlRevisionReason.BeforeExecute,
        SqlCaptureKind.EditorClosed => SqlRevisionReason.EditorClosed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
