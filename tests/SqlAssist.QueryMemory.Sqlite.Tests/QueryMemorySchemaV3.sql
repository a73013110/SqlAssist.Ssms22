-- 固定 v3 增量；搭配固定 v1／v2 fixture 使用，不從最新產品 schema 產生。
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
PRAGMA user_version=3;
