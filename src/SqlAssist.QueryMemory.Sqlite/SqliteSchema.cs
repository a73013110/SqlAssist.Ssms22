namespace SqlAssist.QueryMemory.Sqlite;

internal static class SqliteSchema
{
    public const int Version = 1;
    public const int ApplicationId = 0x53514c4d;

    // 外鍵延後到 commit，才能在同一交易建立 Session 與指向它的第一份 Revision。
    public const string Create = @"
CREATE TABLE StoreInfo (StoreId TEXT NOT NULL);
CREATE TABLE Documents (
    DocumentId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, FilePath TEXT
);
CREATE TABLE Contexts (
    ContextId TEXT PRIMARY KEY, Server TEXT NOT NULL, DatabaseName TEXT NOT NULL, IdentityName TEXT
);
CREATE TABLE Contents (
    ContentId TEXT PRIMARY KEY, ContentHash TEXT NOT NULL, SqlBytes BLOB NOT NULL,
    Length INTEGER NOT NULL CHECK(Length >= 0 AND length(SqlBytes) = 2 * Length), Preview TEXT NOT NULL
);
CREATE TABLE Sessions (
    SessionId TEXT PRIMARY KEY, DocumentId TEXT NOT NULL REFERENCES Documents(DocumentId),
    StartedAt INTEGER NOT NULL, ClosedAt INTEGER, Version INTEGER NOT NULL CHECK(Version > 0),
    LastSequence INTEGER NOT NULL CHECK(LastSequence > 0),
    LatestRevisionId TEXT REFERENCES Revisions(RevisionId) DEFERRABLE INITIALLY DEFERRED,
    LatestExecutionRevisionId TEXT REFERENCES Revisions(RevisionId) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE Revisions (
    RevisionId TEXT PRIMARY KEY,
    ParentRevisionId TEXT REFERENCES Revisions(RevisionId),
    ContentId TEXT NOT NULL REFERENCES Contents(ContentId),
    SessionId TEXT NOT NULL REFERENCES Sessions(SessionId) DEFERRABLE INITIALLY DEFERRED,
    CreatedAt INTEGER NOT NULL, Reason INTEGER NOT NULL,
    ContextId TEXT REFERENCES Contexts(ContextId), IsExecutionSelection INTEGER NOT NULL
);
CREATE TABLE Executions (
    ExecutionId TEXT PRIMARY KEY, RevisionId TEXT NOT NULL REFERENCES Revisions(RevisionId),
    ExecutedAt INTEGER NOT NULL, ContextId TEXT REFERENCES Contexts(ContextId),
    Scope INTEGER NOT NULL, Status INTEGER NOT NULL, Duration INTEGER
);
CREATE TABLE Recovery (
    SessionId TEXT PRIMARY KEY REFERENCES Sessions(SessionId), ContentId TEXT NOT NULL REFERENCES Contents(ContentId),
    Sequence INTEGER NOT NULL, CapturedAt INTEGER NOT NULL, ContextId TEXT REFERENCES Contexts(ContextId)
);
CREATE TABLE Captures (
    CaptureId TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(SessionId), Sequence INTEGER NOT NULL,
    UNIQUE(SessionId, Sequence)
);
CREATE TABLE History (
    EntryKey TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(SessionId) DEFERRABLE INITIALLY DEFERRED,
    RevisionId TEXT REFERENCES Revisions(RevisionId), ContentId TEXT NOT NULL REFERENCES Contents(ContentId),
    CreatedAt INTEGER NOT NULL, Kind INTEGER NOT NULL CHECK(Kind IN (1, 2)),
    ContextId TEXT REFERENCES Contexts(ContextId), Server TEXT, DatabaseName TEXT,
    Pinned INTEGER NOT NULL DEFAULT 0 CHECK(Pinned IN (0, 1))
);
CREATE INDEX IX_History_Time ON History(CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_KindTime ON History(Kind, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_ServerTime ON History(Server, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_ServerDatabaseTime ON History(Server, DatabaseName, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_DatabaseTime ON History(DatabaseName, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_PinnedTime ON History(CreatedAt DESC, EntryKey DESC) WHERE Pinned = 1;
CREATE INDEX IX_Revisions_Session ON Revisions(SessionId, CreatedAt DESC);
CREATE INDEX IX_Revisions_Content ON Revisions(ContentId);
CREATE INDEX IX_Recovery_Content ON Recovery(ContentId);
CREATE INDEX IX_History_Content ON History(ContentId);
CREATE INDEX IX_Executions_Time ON Executions(ExecutedAt DESC);
";
}
