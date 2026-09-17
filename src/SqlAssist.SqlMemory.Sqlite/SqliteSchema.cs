namespace SqlAssist.SqlMemory.Sqlite;

internal static class SqliteSchema
{
    public const int Version = 5;
    public const int ApplicationId = 0x534d454d;
    public const string MaintenanceLeaseId = "maintenance";

    // 延後外鍵允許同一交易建立 Session 與第一個 Revision；收藏版本標記不設外鍵，刪除收藏不改寫版本。
    // 版本要嘛屬於一次編輯器生命週期，要嘛屬於一個收藏；兩者皆空的版本沒有任何配額界線可套用。
    // 連線只存在使用者看得到的兩處：History 投影與收藏標註；版本、執行與 Recovery 不各留一份沒有人讀的連線。
    // 同一 Session 連續執行同一份內容與連線時合併成一列 History；Executions 仍逐次保存並以 EntryKey 指回那一列，
    // Sessions.LatestExecutionEntryKey 只是找「上一筆執行列」的指標，列被刪掉後自然失效，所以不設外鍵。
    public const string Create = @"
CREATE TABLE StoreInfo (StoreId TEXT NOT NULL);
CREATE TABLE Documents (
    DocumentId TEXT PRIMARY KEY, DisplayName TEXT NOT NULL
);
CREATE TABLE Contents (
    ContentId TEXT PRIMARY KEY, SqlBytes BLOB NOT NULL,
    Length INTEGER NOT NULL CHECK(Length >= 0 AND length(SqlBytes) = 2 * Length), Preview TEXT NOT NULL
);
CREATE TABLE Sessions (
    SessionId TEXT PRIMARY KEY, DocumentId TEXT NOT NULL REFERENCES Documents(DocumentId),
    ClosedAt INTEGER, Version INTEGER NOT NULL CHECK(Version > 0),
    LastSequence INTEGER NOT NULL CHECK(LastSequence > 0),
    LatestRevisionId TEXT REFERENCES Revisions(RevisionId) DEFERRABLE INITIALLY DEFERRED,
    LatestExecutionRevisionId TEXT REFERENCES Revisions(RevisionId) DEFERRABLE INITIALLY DEFERRED,
    LatestExecutionEntryKey TEXT,
    LeaseId TEXT REFERENCES Leases(LeaseId)
);
CREATE TABLE Revisions (
    RevisionId TEXT PRIMARY KEY,
    ParentRevisionId TEXT REFERENCES Revisions(RevisionId),
    ContentId TEXT NOT NULL REFERENCES Contents(ContentId),
    SessionId TEXT REFERENCES Sessions(SessionId) DEFERRABLE INITIALLY DEFERRED,
    CreatedAt INTEGER NOT NULL, Reason INTEGER NOT NULL, IsExecutionSelection INTEGER NOT NULL,
    FavoriteId TEXT,
    CHECK(SessionId IS NOT NULL OR FavoriteId IS NOT NULL)
);
CREATE TABLE Executions (
    ExecutionId TEXT PRIMARY KEY, RevisionId TEXT NOT NULL REFERENCES Revisions(RevisionId),
    ExecutedAt INTEGER NOT NULL,
    EntryKey TEXT NOT NULL REFERENCES History(EntryKey) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE Recovery (
    SessionId TEXT PRIMARY KEY REFERENCES Sessions(SessionId), ContentId TEXT NOT NULL REFERENCES Contents(ContentId),
    CapturedAt INTEGER NOT NULL
);
CREATE TABLE Captures (
    CaptureId TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(SessionId), Sequence INTEGER NOT NULL,
    UNIQUE(SessionId, Sequence)
);
CREATE TABLE History (
    EntryKey TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES Sessions(SessionId) DEFERRABLE INITIALLY DEFERRED,
    RevisionId TEXT REFERENCES Revisions(RevisionId), ContentId TEXT NOT NULL REFERENCES Contents(ContentId),
    CreatedAt INTEGER NOT NULL, Kind INTEGER NOT NULL CHECK(Kind IN (1, 2)),
    Server TEXT, DatabaseName TEXT,
    ExecutionCount INTEGER NOT NULL CHECK(ExecutionCount > 0), FirstExecutedAt INTEGER,
    CHECK((Kind=1) = (FirstExecutedAt IS NOT NULL) AND (Kind=1 OR ExecutionCount=1))
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

-- 伺服器與資料庫是各自獨立的標註，不是階層；四個時間索引對應 History 的同一組篩選組合，清單一律沿索引串流。
CREATE TABLE Favorites (
    FavoriteId TEXT PRIMARY KEY, Name TEXT NOT NULL, Description TEXT,
    CurrentRevisionId TEXT NOT NULL REFERENCES Revisions(RevisionId),
    Server TEXT CHECK(Server <> ''), DatabaseName TEXT CHECK(DatabaseName <> ''),
    UpdatedAt INTEGER NOT NULL, Version TEXT NOT NULL
);
CREATE INDEX IX_Favorites_Time ON Favorites(UpdatedAt DESC, FavoriteId DESC);
CREATE INDEX IX_Favorites_ServerTime ON Favorites(Server, UpdatedAt DESC, FavoriteId DESC);
CREATE INDEX IX_Favorites_ServerDatabaseTime ON Favorites(Server, DatabaseName, UpdatedAt DESC, FavoriteId DESC);
CREATE INDEX IX_Favorites_DatabaseTime ON Favorites(DatabaseName, UpdatedAt DESC, FavoriteId DESC);
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
CREATE INDEX IX_Sessions_Head ON Sessions(LatestRevisionId);
CREATE INDEX IX_Sessions_ExecutionHead ON Sessions(LatestExecutionRevisionId);
CREATE INDEX IX_Executions_Revision ON Executions(RevisionId);
CREATE INDEX IX_Executions_Entry ON Executions(EntryKey, ExecutedAt, ExecutionId);
CREATE INDEX IX_History_Revision ON History(RevisionId);

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
