using System;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Core.Tests.SqlMemory;

internal static class SqlMemoryTestData
{
    public static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    public static readonly QueryDocument Document = new(Guid.NewGuid(), "SQLQuery1.sql", null);
    public static readonly QuerySession Session = new(Guid.NewGuid(), Document.DocumentId, Start);
    public static readonly SqlCapturePolicy Policy = new(true, true, TimeSpan.FromMinutes(10), true, true);

    public static SqlCapture Capture(long sequence = 1, string text = "SELECT * FROM Lib_Reader;",
        SqlCaptureKind kind = SqlCaptureKind.DraftIdle, int seconds = 0, string? selection = null,
        SqlConnectionLabel? connection = null, QuerySession? session = null) =>
        new(Guid.NewGuid(), Document, session ?? Session, sequence, Start.AddSeconds(seconds), kind,
            new QueryTextSnapshot(text), connection, selection == null ? null : new QueryTextSnapshot(selection));
}
