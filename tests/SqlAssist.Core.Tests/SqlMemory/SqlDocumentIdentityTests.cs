using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlDocumentIdentityTests
{
    private const string LoanScript = @"C:\Library\Scripts\Loan.sql";

    [Fact]
    public void UnsavedWindowsAreSeparateDocumentsEvenWithTheSameTitle()
    {
        var first = new SqlDocumentIdentity("SQLQuery1.sql", "SQLQuery1.sql");
        var second = new SqlDocumentIdentity("SQLQuery1.sql", null);

        Assert.Null(first.Document.FilePath);
        Assert.NotEqual(first.Document.DocumentId, second.Document.DocumentId);
        Assert.Equal(first.Document.DocumentId, first.Session.DocumentId);
    }

    [Fact]
    public void TheSameFileIsTheSameDocumentAcrossWindowsButEachWindowIsANewSession()
    {
        var first = new SqlDocumentIdentity("Loan.sql", LoanScript);
        var second = new SqlDocumentIdentity("LOAN.SQL", LoanScript.ToUpperInvariant());

        Assert.Equal(first.Document.DocumentId, second.Document.DocumentId);
        Assert.NotEqual(first.Session.SessionId, second.Session.SessionId);
    }

    /// <summary>未存檔查詢第一次存檔：歷程改掛在檔案那份文件上，舊 Session 交給呼叫端正式關閉。</summary>
    [Fact]
    public void SavingAnUnsavedQueryHandsTheOldSessionOverAndStartsOneOnTheFileDocument()
    {
        var identity = new SqlDocumentIdentity("SQLQuery1.sql", null);
        var unsaved = identity.Document;
        var session = identity.Session;
        Assert.Equal(1, identity.NextSequence());
        Assert.Equal(2, identity.NextSequence());

        var handover = identity.Observe("Loan.sql", LoanScript);

        Assert.NotNull(handover);
        Assert.Equal(unsaved, handover!.Document);
        Assert.Equal(session, handover.Session);
        Assert.Equal(3, handover.Sequence);
        Assert.Equal(("Loan.sql", LoanScript), (identity.Document.DisplayName, identity.Document.FilePath));
        Assert.Equal(SqlDocumentIdentity.DocumentId(LoanScript), identity.Document.DocumentId);
        Assert.Equal(identity.Document.DocumentId, identity.Session.DocumentId);
        Assert.NotEqual(session.SessionId, identity.Session.SessionId);
        Assert.Equal(1, identity.NextSequence());
    }

    [Fact]
    public void SaveAsMovesToTheNewPathWhileRenamingOnlyTheTitleKeepsTheSession()
    {
        var identity = new SqlDocumentIdentity("Loan.sql", LoanScript);
        var session = identity.Session;
        identity.NextSequence();

        Assert.Null(identity.Observe("Loan.sql", LoanScript));
        Assert.Null(identity.Observe("Loan.sql*", LoanScript.ToUpperInvariant()));
        Assert.Equal(session, identity.Session);
        Assert.Equal("Loan.sql*", identity.Document.DisplayName);

        var moved = identity.Observe("LoanDetail.sql", @"C:\Library\Scripts\LoanDetail.sql");
        Assert.NotNull(moved);
        Assert.Equal(session, moved!.Session);
        Assert.NotEqual(session.SessionId, identity.Session.SessionId);
    }

    /// <summary>還沒擷取過就存檔：沒有舊 Session 的列要關，直接換到檔案那份文件，不替暫存標題造出關閉版本。</summary>
    [Fact]
    public void SavingBeforeAnyCaptureSwitchesDocumentsWithoutAHandover()
    {
        var identity = new SqlDocumentIdentity("SQLQuery1.sql", null);
        var session = identity.Session;
        Assert.False(identity.HasCaptures);

        Assert.Null(identity.Observe("Loan.sql", LoanScript));

        Assert.Equal(LoanScript, identity.Document.FilePath);
        Assert.NotEqual(session.SessionId, identity.Session.SessionId);
        Assert.Equal(1, identity.NextSequence());
        Assert.True(identity.HasCaptures);
    }

    [Fact]
    public void StaleIdentityIsDetectedWithoutChangingTheSession()
    {
        var identity = new SqlDocumentIdentity("SQLQuery1.sql", null);
        var session = identity.Session;

        Assert.False(identity.IsStale("SQLQuery1.sql", "SQLQuery1.sql"));
        Assert.True(identity.IsStale("Loan.sql", LoanScript));
        Assert.True(identity.IsStale("SQLQuery2.sql", null));
        Assert.Equal(session, identity.Session);
    }

    [Fact]
    public void TitlesAreNeverEmpty()
    {
        Assert.Equal("SQL 查詢", new SqlDocumentIdentity(" ", null).Document.DisplayName);
    }
}
