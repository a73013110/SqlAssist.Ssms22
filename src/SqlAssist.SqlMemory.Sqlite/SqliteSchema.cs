namespace SqlAssist.SqlMemory.Sqlite;

internal static class SqliteSchema
{
    public const int Version = 3;
    public const int ApplicationId = 0x534d454d;
    public const string MaintenanceLeaseId = "maintenance";

    // 延後外鍵允許同一交易建立 Session 與第一個 Revision；收藏版本標記不設外鍵，刪除收藏不改寫版本。
    // 版本要嘛屬於一次編輯器生命週期，要嘛屬於一個收藏；兩者皆空的版本沒有任何配額界線可套用。
    public const string Create = @"
CREATE TABLE StoreInfo (StoreId TEXT NOT NULL);
CREATE TABLE Documents (
    DocumentId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, FilePath TEXT
);
CREATE TABLE Contexts (
    ContextId TEXT PRIMARY KEY, Server TEXT NOT NULL, DatabaseName TEXT NOT NULL
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
    LatestExecutionRevisionId TEXT REFERENCES Revisions(RevisionId) DEFERRABLE INITIALLY DEFERRED,
    LeaseId TEXT REFERENCES Leases(LeaseId)
);
CREATE TABLE Revisions (
    RevisionId TEXT PRIMARY KEY,
    ParentRevisionId TEXT REFERENCES Revisions(RevisionId),
    ContentId TEXT NOT NULL REFERENCES Contents(ContentId),
    SessionId TEXT REFERENCES Sessions(SessionId) DEFERRABLE INITIALLY DEFERRED,
    CreatedAt INTEGER NOT NULL, Reason INTEGER NOT NULL,
    ContextId TEXT REFERENCES Contexts(ContextId), IsExecutionSelection INTEGER NOT NULL,
    FavoriteId TEXT,
    CHECK(SessionId IS NOT NULL OR FavoriteId IS NOT NULL)
);
CREATE TABLE Executions (
    ExecutionId TEXT PRIMARY KEY, RevisionId TEXT NOT NULL REFERENCES Revisions(RevisionId),
    ExecutedAt INTEGER NOT NULL, ContextId TEXT REFERENCES Contexts(ContextId),
    Scope INTEGER NOT NULL
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
    ContextId TEXT REFERENCES Contexts(ContextId), Server TEXT, DatabaseName TEXT
);
CREATE INDEX IX_History_Time ON History(CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_KindTime ON History(Kind, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_ServerTime ON History(Server, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_ServerDatabaseTime ON History(Server, DatabaseName, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_History_DatabaseTime ON History(DatabaseName, CreatedAt DESC, EntryKey DESC);
CREATE INDEX IX_Revisions_Content ON Revisions(ContentId);
CREATE INDEX IX_Recovery_Content ON Recovery(ContentId);
CREATE INDEX IX_History_Content ON History(ContentId);
CREATE INDEX IX_Executions_Time ON Executions(ExecutedAt, ExecutionId);

CREATE TABLE Favorites (
    FavoriteId TEXT PRIMARY KEY, Name TEXT NOT NULL, Description TEXT,
    CurrentRevisionId TEXT NOT NULL REFERENCES Revisions(RevisionId),
    Scope INTEGER NOT NULL CHECK(Scope IN (0, 1, 2)),
    ContextId TEXT REFERENCES Contexts(ContextId), Server TEXT, DatabaseName TEXT,
    Version TEXT NOT NULL,
    CHECK((Scope=0 AND ContextId IS NULL AND Server IS NULL AND DatabaseName IS NULL)
       OR (Scope=1 AND ContextId IS NOT NULL AND Server IS NOT NULL AND DatabaseName IS NULL)
       OR (Scope=2 AND ContextId IS NOT NULL AND Server IS NOT NULL AND DatabaseName IS NOT NULL))
);
CREATE INDEX IX_Favorites_ScopeId ON Favorites(Scope, Server, DatabaseName, FavoriteId DESC);
CREATE INDEX IX_Favorites_Revision ON Favorites(CurrentRevisionId);

CREATE TABLE StorageUsage (
    Id INTEGER PRIMARY KEY CHECK(Id=1), ContentBytes INTEGER NOT NULL CHECK(ContentBytes>=0)
);
INSERT INTO StorageUsage VALUES(1,0);
CREATE TRIGGER TR_Contents_Insert AFTER INSERT ON Contents BEGIN
    UPDATE StorageUsage SET ContentBytes=ContentBytes+2 * NEW.Length WHERE Id=1;
END;
CREATE TRIGGER TR_Contents_Delete AFTER DELETE ON Contents BEGIN
    UPDATE StorageUsage SET ContentBytes=ContentBytes-2 * OLD.Length WHERE Id=1;
END;
CREATE TRIGGER TR_Contents_Update AFTER UPDATE OF Length ON Contents BEGIN
    UPDATE StorageUsage SET ContentBytes=ContentBytes+2 * (NEW.Length-OLD.Length) WHERE Id=1;
END;
CREATE INDEX IX_Revisions_Parent ON Revisions(ParentRevisionId);
CREATE INDEX IX_Revisions_Context ON Revisions(ContextId);
CREATE INDEX IX_Sessions_Head ON Sessions(LatestRevisionId);
CREATE INDEX IX_Sessions_ExecutionHead ON Sessions(LatestExecutionRevisionId);
CREATE INDEX IX_Executions_Revision ON Executions(RevisionId);
CREATE INDEX IX_Executions_Context ON Executions(ContextId);
CREATE INDEX IX_History_Revision ON History(RevisionId);
CREATE INDEX IX_History_Context ON History(ContextId);
CREATE INDEX IX_Recovery_Context ON Recovery(ContextId);
CREATE INDEX IX_Favorites_Context ON Favorites(ContextId);

CREATE INDEX IX_Revisions_SessionAuto ON Revisions(SessionId, CreatedAt DESC)
    WHERE Reason=0 AND IsExecutionSelection=0;

CREATE INDEX IX_Revisions_Favorite ON Revisions(FavoriteId, CreatedAt, RevisionId) WHERE FavoriteId IS NOT NULL;

-- 維護候選的時間索引：第二欄是 keyset 的同時間決勝鍵，部分索引條件必須與候選查詢逐字相同才會命中。
CREATE INDEX IX_History_SessionDrafts ON History(SessionId, CreatedAt, EntryKey) WHERE Kind=2 AND RevisionId IS NOT NULL;
CREATE INDEX IX_Revisions_SelectionTime ON Revisions(CreatedAt, RevisionId) WHERE IsExecutionSelection=1;
CREATE INDEX IX_Recovery_Time ON Recovery(CapturedAt, SessionId);

CREATE TABLE Leases (
    LeaseId TEXT PRIMARY KEY, MachineName TEXT NOT NULL, ProcessId INTEGER NOT NULL,
    ProcessStartTime INTEGER NOT NULL, RenewedAt INTEGER NOT NULL
);
CREATE INDEX IX_Leases_Renewed ON Leases(RenewedAt);
CREATE INDEX IX_Sessions_Lease ON Sessions(LeaseId) WHERE LeaseId IS NOT NULL;

CREATE TABLE MaintenanceState (
    Id INTEGER PRIMARY KEY CHECK(Id=1), Version INTEGER NOT NULL CHECK(Version > 0),
    PlanFingerprint TEXT NOT NULL, RoundStartedAt INTEGER NOT NULL, Level INTEGER NOT NULL CHECK(Level >= 0),
    ReclaimsRecovery INTEGER NOT NULL CHECK(ReclaimsRecovery IN (0, 1)), Scan INTEGER NOT NULL CHECK(Scan IN (0, 1)),
    RoundsSinceFullScan INTEGER NOT NULL CHECK(RoundsSinceFullScan >= 0), Cursor TEXT,
    RequiresAnotherPass INTEGER NOT NULL CHECK(RequiresAnotherPass IN (0, 1)),
    CapacityStatus INTEGER NOT NULL CHECK(CapacityStatus IN (0, 1, 2))
);
";
}
