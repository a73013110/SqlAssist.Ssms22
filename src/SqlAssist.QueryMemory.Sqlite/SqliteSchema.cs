namespace SqlAssist.QueryMemory.Sqlite;

internal static class SqliteSchema
{
    public const int Version = 6;
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

    // 保留 v1 原始建表流程；新庫也逐版升級，避免兩套 schema 隨時間分岔。
    public const string Migrate1To2 = @"
CREATE TABLE SavedQueries (
    SavedQueryId TEXT PRIMARY KEY, Name TEXT NOT NULL, Description TEXT,
    CurrentRevisionId TEXT NOT NULL REFERENCES Revisions(RevisionId),
    Scope INTEGER NOT NULL CHECK(Scope IN (0, 1, 2)),
    ContextId TEXT REFERENCES Contexts(ContextId), Server TEXT, DatabaseName TEXT,
    Pinned INTEGER NOT NULL CHECK(Pinned IN (0, 1)), Version TEXT NOT NULL,
    CHECK((Scope=0 AND ContextId IS NULL AND Server IS NULL AND DatabaseName IS NULL)
       OR (Scope=1 AND ContextId IS NOT NULL AND Server IS NOT NULL AND DatabaseName IS NULL)
       OR (Scope=2 AND ContextId IS NOT NULL AND Server IS NOT NULL AND DatabaseName IS NOT NULL))
);
CREATE INDEX IX_SavedQueries_ScopeId ON SavedQueries(Scope, Server, DatabaseName, SavedQueryId DESC);
CREATE INDEX IX_SavedQueries_Revision ON SavedQueries(CurrentRevisionId);
";

    // 一次性 migration 建立計量與引用索引；日常維護不能每批 SUM 全庫或掃描外鍵來源。
    public const string Migrate2To3 = @"
CREATE TABLE StorageUsage (
    Id INTEGER PRIMARY KEY CHECK(Id=1), ContentBytes INTEGER NOT NULL CHECK(ContentBytes>=0)
);
INSERT INTO StorageUsage SELECT 1,COALESCE(SUM(2 * Length),0) FROM Contents;
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
CREATE INDEX IX_History_Revision ON History(RevisionId,Pinned);
CREATE INDEX IX_History_Context ON History(ContextId);
CREATE INDEX IX_Recovery_Context ON Recovery(ContextId);
CREATE INDEX IX_SavedQueries_Context ON SavedQueries(ContextId);
";

    // 每 Session auto revision 配額只能掃描前 N 筆索引項；SQLite 要看到常數條件才會採用部分索引。
    public const string Migrate3To4 = @"
CREATE INDEX IX_Revisions_SessionAuto ON Revisions(SessionId, CreatedAt DESC)
    WHERE Reason=0 AND IsExecutionSelection=0;
";

    // 每 Saved 版本配額要知道版本屬於哪個收藏；ADD COLUMN 不重建資料表，既有列維持 NULL。
    // 不設外鍵：加了就得在刪除收藏時連帶刪版本或改寫不可變列，改由維護把失去收藏的版本按草稿期限回收。
    public const string Migrate4To5 = @"
ALTER TABLE Revisions ADD COLUMN SavedQueryId TEXT;
CREATE INDEX IX_Revisions_Saved ON Revisions(SavedQueryId, CreatedAt DESC) WHERE SavedQueryId IS NOT NULL;
";

    /// <summary>維護租約用的保留識別碼；32 位十六進位的 Session 租約不可能撞到這個值。</summary>
    public const string MaintenanceLeaseId = "maintenance";

    // Session 心跳與跨程序維護租約共用這張表：一個程序只續一列，不必每個 Session 各存一份三元組。
    // LeaseId 設外鍵，釋放時必須先解除 Session 標記才刪得掉租約列，回收不會留下指向空租約的 Session。
    public const string Migrate5To6 = @"
CREATE TABLE Leases (
    LeaseId TEXT PRIMARY KEY, MachineName TEXT NOT NULL, ProcessId INTEGER NOT NULL,
    ProcessStartTime INTEGER NOT NULL, RenewedAt INTEGER NOT NULL
);
CREATE INDEX IX_Leases_Renewed ON Leases(RenewedAt);
ALTER TABLE Sessions ADD COLUMN LeaseId TEXT REFERENCES Leases(LeaseId);
CREATE INDEX IX_Sessions_Lease ON Sessions(LeaseId) WHERE LeaseId IS NOT NULL;
";
}
