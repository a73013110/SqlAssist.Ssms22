-- 固定 v4 增量；搭配固定 v1～v3 fixture 使用，不從最新產品 schema 產生。
CREATE INDEX IX_Revisions_SessionAuto ON Revisions(SessionId, CreatedAt DESC)
    WHERE Reason=0 AND IsExecutionSelection=0;
PRAGMA user_version=4;
