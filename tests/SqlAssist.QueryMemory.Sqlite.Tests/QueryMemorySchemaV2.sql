-- 固定 v2 增量；搭配固定 v1 fixture 使用，不從最新產品 schema 產生。
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
PRAGMA user_version=2;
