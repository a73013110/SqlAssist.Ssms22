using System;
using SqlAssist.Core.Connections;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Core.Tests.SqlMemory;

internal static class SqlMemoryTestData
{
    public static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    public static readonly SqlDocument Document = new(Guid.NewGuid(), "SQLQuery1.sql", null);
    public static readonly SqlSession Session = new(Guid.NewGuid(), Document.DocumentId);
    public static readonly SqlCapturePolicy Policy = new(true, true, TimeSpan.FromMinutes(10), true, true);

    public static SqlCapture Capture(long sequence = 1, string text = "SELECT * FROM Lib_Reader;",
        SqlCaptureKind kind = SqlCaptureKind.DraftIdle, int seconds = 0, string? selection = null,
        SqlConnectionLabel? connection = null, SqlSession? session = null) =>
        new(Guid.NewGuid(), Document, session ?? Session, sequence, Start.AddSeconds(seconds), kind,
            new SqlTextSnapshot(text), connection, selection == null ? null : new SqlTextSnapshot(selection));
}
