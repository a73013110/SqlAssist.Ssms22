using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QueryDocumentIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
    private const string LoanScript = @"C:\Library\Scripts\Loan.sql";

    [Fact]
    public void UnsavedWindowsAreSeparateDocumentsEvenWithTheSameTitle()
    {
        var first = new QueryDocumentIdentity("SQLQuery1.sql", "SQLQuery1.sql", Now);
        var second = new QueryDocumentIdentity("SQLQuery1.sql", null, Now);

        Assert.Null(first.Document.FilePath);
        Assert.NotEqual(first.Document.DocumentId, second.Document.DocumentId);
        Assert.Equal(first.Document.DocumentId, first.Session.DocumentId);
    }

    [Fact]
    public void TheSameFileIsTheSameDocumentAcrossWindowsButEachWindowIsANewSession()
    {
        var first = new QueryDocumentIdentity("Loan.sql", LoanScript, Now);
        var second = new QueryDocumentIdentity("LOAN.SQL", LoanScript.ToUpperInvariant(), Now);

        Assert.Equal(first.Document.DocumentId, second.Document.DocumentId);
        Assert.NotEqual(first.Session.SessionId, second.Session.SessionId);
    }

    /// <summary>未存檔查詢第一次存檔：歷程改掛在檔案那份文件上，舊 Session 交給呼叫端正式關閉。</summary>
    [Fact]
    public void SavingAnUnsavedQueryHandsTheOldSessionOverAndStartsOneOnTheFileDocument()
    {
        var identity = new QueryDocumentIdentity("SQLQuery1.sql", null, Now);
        var unsaved = identity.Document;
        var session = identity.Session;
        Assert.Equal(1, identity.NextSequence());
        Assert.Equal(2, identity.NextSequence());

        var handover = identity.Observe("Loan.sql", LoanScript, Now.AddMinutes(5));

        Assert.NotNull(handover);
        Assert.Equal(unsaved, handover!.Document);
        Assert.Equal(session, handover.Session);
        Assert.Equal(3, handover.Sequence);
        Assert.Equal(("Loan.sql", LoanScript), (identity.Document.DisplayName, identity.Document.FilePath));
        Assert.Equal(QueryDocumentIdentity.DocumentId(LoanScript), identity.Document.DocumentId);
        Assert.Equal(identity.Document.DocumentId, identity.Session.DocumentId);
        Assert.NotEqual(session.SessionId, identity.Session.SessionId);
        Assert.Equal(Now.AddMinutes(5), identity.Session.StartedAt);
        Assert.Equal(1, identity.NextSequence());
    }

    [Fact]
    public void SaveAsMovesToTheNewPathWhileRenamingOnlyTheTitleKeepsTheSession()
    {
        var identity = new QueryDocumentIdentity("Loan.sql", LoanScript, Now);
        var session = identity.Session;

        Assert.Null(identity.Observe("Loan.sql", LoanScript, Now));
        Assert.Null(identity.Observe("Loan.sql*", LoanScript.ToUpperInvariant(), Now));
        Assert.Equal(session, identity.Session);
        Assert.Equal("Loan.sql*", identity.Document.DisplayName);

        var moved = identity.Observe("LoanDetail.sql", @"C:\Library\Scripts\LoanDetail.sql", Now);
        Assert.NotNull(moved);
        Assert.Equal(session, moved!.Session);
        Assert.NotEqual(session.SessionId, identity.Session.SessionId);
    }

    [Fact]
    public void TitlesAreNeverEmpty()
    {
        Assert.Equal("SQL 查詢", new QueryDocumentIdentity(" ", null, Now).Document.DisplayName);
    }
}
