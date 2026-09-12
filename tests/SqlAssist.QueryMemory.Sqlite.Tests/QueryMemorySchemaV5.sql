-- 固定 v5 增量；搭配固定 v1～v4 fixture 使用，不從最新產品 schema 產生。
ALTER TABLE Revisions ADD COLUMN SavedQueryId TEXT;
CREATE INDEX IX_Revisions_Saved ON Revisions(SavedQueryId, CreatedAt DESC) WHERE SavedQueryId IS NOT NULL;
PRAGMA user_version=5;
