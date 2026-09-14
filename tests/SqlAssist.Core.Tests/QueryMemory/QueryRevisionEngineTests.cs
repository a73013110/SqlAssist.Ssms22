using System;
using SqlAssist.Core.QueryMemory;
using Xunit;
using static SqlAssist.Core.Tests.QueryMemory.QueryMemoryTestData;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryRevisionEngineTests
{
    private readonly QueryRevisionEngine _engine = new();

    [Fact]
    public void FrequentDraftsReplaceRecoveryWithoutGrowingTimeline()
    {
        var first = Prepare(Capture());
        Assert.Single(first.Revisions);
        var second = Prepare(Capture(2, "SELECT * FROM Lib_Tag;", seconds: 5), first.State);
        Assert.Empty(second.Revisions);
        Assert.NotEqual(first.Recovery?.ContentId, second.Recovery?.ContentId);
        Assert.Equal(first.State.LatestRevision, second.State.LatestRevision);
        Assert.Equal(2, second.Recovery?.Sequence);
        Assert.Equal(first.State.Version, second.ExpectedVersion);
    }

    [Fact]
    public void AutoCheckpointRequiresChangedContentAndElapsedInterval()
    {
        var first = Prepare(Capture());
        var unchanged = Prepare(Capture(2, seconds: 600), first.State);
        Assert.Empty(unchanged.Revisions);
        var changed = Prepare(Capture(3, "SELECT * FROM Lib_Tag;", seconds: 600), unchanged.State);
        var revision = Assert.Single(changed.Revisions);
        Assert.Equal(QueryRevisionReason.AutoCheckpoint, revision.Reason);
        Assert.Equal(first.State.LatestRevision?.RevisionId, revision.ParentRevisionId);
    }

    [Fact]
    public void TwentyExecutionsReuseOneRevisionAndDoNotFreezeConnection()
    {
        QuerySessionState? state = null;
        Guid? revisionId = null;
        for (var i = 1; i <= 20; i++)
        {
            var context = new QueryConnectionContext("LibraryServer", "Library" + i);
            var capture = Capture(i, kind: QueryCaptureKind.BeforeExecute, connection: context);
            var write = Prepare(capture, state);
            Assert.NotNull(write.Execution);
            Assert.Equal(capture.CaptureId, write.Execution.ExecutionId);
            Assert.Equal(context, write.Execution.Connection);
            if (i == 1) revisionId = Assert.Single(write.Revisions).RevisionId;
            else Assert.Empty(write.Revisions);
            Assert.Equal(revisionId, write.Execution.RevisionId);
            state = write.State;
        }
    }

    [Fact]
    public void SelectionNeverReplacesDocumentHeadOrRecoveryAndCanReuseRevision()
    {
        var first = Prepare(Capture(kind: QueryCaptureKind.BeforeExecute, selection: "SELECT 1"));
        Assert.Equal(2, first.Revisions.Count);
        Assert.Equal(first.State.LatestRevision?.ContentId, first.Recovery?.ContentId);
        Assert.NotEqual(first.State.LatestRevision?.RevisionId, first.Execution?.RevisionId);
        Assert.True(first.State.LatestExecutionRevision?.IsExecutionSelection);
        var second = Prepare(Capture(2, kind: QueryCaptureKind.BeforeExecute, selection: "SELECT 1"), first.State);
        Assert.Empty(second.Revisions);
        Assert.Equal(first.Execution?.RevisionId, second.Execution?.RevisionId);
    }

    [Fact]
    public void SelectingEntireDocumentReusesDocumentRevision()
    {
        var write = Prepare(Capture(text: "SELECT 1", kind: QueryCaptureKind.BeforeExecute, selection: "SELECT 1"));
        Assert.Single(write.Revisions);
        Assert.Single(write.Contents);
        Assert.Equal(write.State.LatestRevision?.RevisionId, write.Execution?.RevisionId);
        Assert.Equal(QueryExecutionScope.Selection, write.Execution?.Scope);
    }

    [Fact]
    public void CloseCommitsFinalRevisionAndDeletesRecoveryAtomically()
    {
        var first = Prepare(Capture());
        var close = Prepare(Capture(2, "SELECT * FROM Lib_Tag;", QueryCaptureKind.EditorClosed, seconds: 5), first.State);
        Assert.True(close.DeleteRecovery);
        Assert.Null(close.Recovery);
        Assert.Equal(Start.AddSeconds(5), close.State.Session.ClosedAt);
        Assert.Equal(QueryRevisionReason.EditorClosed, Assert.Single(close.Revisions).Reason);
        Assert.Null(_engine.Prepare(Capture(3, seconds: 10), close.State, Policy));
    }

    [Fact]
    public void StaleCaptureCannotOverwriteNewerSnapshotEvenWithLaterClock()
    {
        var first = Prepare(Capture(5));
        Assert.Null(_engine.Prepare(Capture(4, seconds: 60), first.State, Policy));
        Assert.Null(_engine.Prepare(Capture(5, seconds: 120), first.State, Policy));
    }

    [Fact]
    public void SessionsOfSameDocumentHaveIndependentHeads()
    {
        var first = Prepare(Capture());
        var secondSession = new QuerySession(Guid.NewGuid(), Document.DocumentId, Start);
        var second = Prepare(Capture(session: secondSession));
        Assert.NotEqual(first.State.Session.SessionId, second.State.Session.SessionId);
        Assert.Equal(first.Contents[0].ContentId, second.Contents[0].ContentId);
        Assert.Null(second.State.LatestRevision?.ParentRevisionId);
        Assert.Throws<ArgumentException>(() => _engine.Prepare(Capture(session: secondSession), first.State, Policy));
    }

    [Fact]
    public void DisabledDraftCaptureDoesNotLeakUnexecutedTextOnSelectionOrClose()
    {
        var policy = new QueryMemoryPolicy(true, true, TimeSpan.FromMinutes(10), true, false);
        Assert.Null(_engine.Prepare(Capture(), null, policy));
        var write = _engine.Prepare(Capture(kind: QueryCaptureKind.BeforeExecute, selection: "SELECT 1"), null, policy);
        Assert.NotNull(write);
        Assert.Equal("SELECT 1", Assert.Single(write.Contents).SqlText);
        Assert.Null(write.Recovery);
        Assert.Null(write.State.LatestRevision);
        var close = _engine.Prepare(Capture(2, kind: QueryCaptureKind.EditorClosed), write.State, policy);
        Assert.NotNull(close);
        Assert.Empty(close.Contents);
        Assert.Empty(close.Revisions);
        Assert.NotNull(close.State.Session.ClosedAt);
    }

    [Fact]
    public void CaptureSwitchesAndRecoverySwitchAreIndependent()
    {
        var policy = new QueryMemoryPolicy(false, false, TimeSpan.FromMinutes(10), false, true);
        Assert.Null(_engine.Prepare(Capture(kind: QueryCaptureKind.BeforeExecute), null, policy));
        var draft = _engine.Prepare(Capture(), null, policy);
        Assert.NotNull(draft);
        Assert.Null(draft.Recovery);
        Assert.Empty(draft.Revisions);
        var close = _engine.Prepare(Capture(2, kind: QueryCaptureKind.EditorClosed), draft.State, policy);
        Assert.NotNull(close);
        Assert.Single(close.Revisions);
    }

    [Fact]
    public void ExecuteResetsAutoCheckpointSamplingBaseline()
    {
        var first = Prepare(Capture());
        var execute = Prepare(Capture(2, "SELECT 1", QueryCaptureKind.BeforeExecute, seconds: 590), first.State);
        var draft = Prepare(Capture(3, "SELECT 2", seconds: 600), execute.State);
        Assert.Empty(draft.Revisions);
    }

    private QueryMemoryWrite Prepare(QueryMemoryCapture capture, QuerySessionState? previous = null) =>
        Assert.IsType<QueryMemoryWrite>(_engine.Prepare(capture, previous, Policy));
}
