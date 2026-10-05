namespace SqlAssist.KeywordGenerator.Data;

/// <summary>第三階段定位置的樣板。</summary>
/// <remarks>
/// 唯一手寫的部分：每個位置一個樣板，"洞" 就是樣板的結尾。
/// 名稱必須與 SqlKeywordPosition 的成員一致。
///
/// 樣板一律切在「游標前一個詞元」的後面，因為那正是執行期的分析器認得的東西。
/// 樣板本身也要是合法的 T-SQL 片段：WHERE a 之後接 AND 在 T-SQL 裡是錯的
/// （a 不是布林運算式），所以 ExpressionTail 的樣板必須寫成 WHERE a = 1。
///
/// 順序就是產物裡位置旗標的順序；片語的 After 以名稱指到這裡。
/// </remarks>
internal static class PositionTemplates
{
    internal static readonly PositionTemplate[] All =
    [
        // 批次的第一句可以省略 EXEC，普通名稱在那裡也合法；分號之後才分得出 THROW 是關鍵字。
        new("StatementStart", "", "SELECT 1; "),
        new("SelectList", "SELECT "),

        // TOP 子句寫完之後，分析器同時回報這一格與 SelectList；PERCENT、WITH TIES 只在這裡。
        // TOP (10) 得到的字與 TOP 10 相同，不必另列。
        new("TopClauseTail", "SELECT TOP 10 "),
        new("SelectListTail", "SELECT a "),
        new("DataSource", "SELECT * FROM "),

        // 分析器只知道「前一個詞元是識別字」，分不出那個識別字是資料表、
        // 聯結對象還是授權目標。目錄跟著這個粒度走，不假裝分得出來。
        new("TableSourceTail",
            "SELECT * FROM t ", "SELECT * FROM t JOIN y ",
            "INSERT INTO t ", "MERGE INTO t "),

        // 同一個子句錨點在別的敘述裡寫完之後接的字不同：SELECT … INTO 的新資料表之後接 FROM，
        // FETCH 的游標之後接 INTO，UPDATE 的 SET 指派之後接 FROM、WHERE、OUTPUT。
        new("SelectIntoTail", "SELECT a INTO t "),
        new("FetchTail", "FETCH NEXT FROM c "),
        new("UpdateSetTail", "UPDATE t SET a = 1 "),

        // 索引鍵清單裡的資料行之後：ASC、DESC。CREATE INDEX 的 WITH ( 之後是選項，多半不是關鍵字，由子句片語給。
        new("IndexKeyTail", "CREATE INDEX i ON t (a "),
        new("IndexOption", "CREATE INDEX i ON t (a) WITH (", "CREATE INDEX i ON t (a) WITH (ONLINE = ON, "),

        // GRANT／DENY／REVOKE：權限寫完之後是 ON、TO，REVOKE 還有 FROM；ON 之後是類別（SCHEMA::）或目標，
        // 目標寫完之後是 TO、FROM。
        // 權限名稱是一串識別字（VIEW DEFINITION），GRANT SELECT 之後什麼非保留字都接得上；
        // 樣板以資料行清單收掉權限，探到的才只有權限之後的字。
        // 資料庫稽核規格括號裡的動作（SELECT ON t BY u）與權限同一種寫法，目標之後是 BY。
        new("PermissionList", "GRANT SELECT (a) ", "REVOKE SELECT (a) ", "ALTER DATABASE AUDIT SPECIFICATION s ADD (SELECT "),
        new("PermissionOn", "GRANT SELECT ON ", "REVOKE SELECT ON ", "ALTER DATABASE AUDIT SPECIFICATION s ADD (SELECT ON "),
        new("PermissionTarget", "GRANT SELECT ON t ", "REVOKE SELECT ON t ", "ALTER DATABASE AUDIT SPECIFICATION s ADD (SELECT ON t "),

        // TO、FROM、BY 之後是主體：名稱由目錄物件給，關鍵字只剩 PUBLIC 與擁有者的 SCHEMA OWNER。
        new("PermissionGrantee", "GRANT SELECT ON t TO ", "REVOKE SELECT ON t FROM ", "ALTER AUTHORIZATION ON OBJECT::t TO ",
            "ALTER DATABASE AUDIT SPECIFICATION s ADD (SELECT ON t BY "),

        // 資料表之後的 TABLESAMPLE (10 是 PERCENT、ROWS；PIVOT 的彙總與 UNPIVOT 的值之後是 FOR，FOR 的資料行之後是 IN。
        new("TableSampleTail", "SELECT * FROM t TABLESAMPLE (10 "),
        new("PivotClause",
            "SELECT * FROM t PIVOT (COUNT(a) ", "SELECT * FROM t PIVOT (COUNT(a) FOR b ",
            "SELECT * FROM t UNPIVOT (a ", "SELECT * FROM t UNPIVOT (a FOR b "),

        // WHERE CURRENT OF 只有 UPDATE 與 DELETE 寫得出來。
        new("Predicate", "SELECT * FROM t WHERE ", "DELETE FROM t WHERE "),

        // 兩個都要：WHERE a 之後是 IN、IS、LIKE、BETWEEN，
        // WHERE a = 1 之後才是 AND、OR 與後續子句。分析器一樣分不出來。
        // LIKE 的樣式寫完之後同樣回報這個位置，ESCAPE 只在那裡。
        // 第一個是代表寫法，子句片語拿它探測。
        new("ExpressionTail",
            "SELECT * FROM t WHERE a = 1 ", "SELECT * FROM t WHERE a ",
            "SELECT * FROM t WHERE a LIKE 'x' "),
        new("OrderByTail", "SELECT * FROM t ORDER BY a "),

        // 查詢 ORDER BY 的 OFFSET 值之後是 ROW、ROWS；視窗 OVER (ORDER BY a 之後是 ASC、DESC 與視窗框架。
        new("OffsetTail", "SELECT * FROM t ORDER BY a OFFSET 10 "),
        new("WindowOrderTail", "SELECT SUM(a) OVER (ORDER BY a "),

        // 視窗規格的括號裡、排序之前：開頭、基底視窗名稱與 PARTITION BY 的一項之後。WINDOW 子句的定義是同一種括號；
        // 一項的名稱之後是 AS，定義寫完之後是逗號或查詢之後的子句。
        new("WindowSpecification",
            "SELECT SUM(a) OVER (", "SELECT SUM(a) OVER (w ", "SELECT SUM(a) OVER (PARTITION BY a ",
            "SELECT a FROM t WINDOW w AS ("),
        new("WindowName", "SELECT a FROM t WINDOW w ", "SELECT a FROM t WINDOW w AS (ORDER BY a), w2 "),
        new("WindowClauseTail", "SELECT a FROM t WINDOW w AS (ORDER BY a) "),

        // 函式呼叫之後的 OVER、COLLATE。包在括號裡，選取清單尾端的 FROM、別名這些字就接不上。
        // 第二個樣板給只有位移函式接得上的 IGNORE NULLS、RESPECT NULLS（SUM(a) IGNORE 剖析器不收）。
        new("FunctionCallTail", "SELECT (SUM(a) ", "SELECT (LAG(a) "),

        // 運算元之後的 COLLATE、AT TIME ZONE。與函式呼叫同一個道理包在括號裡：只留得下接在任何運算式後面的字。
        new("OperandTail", "SELECT (a "),

        // 外部索引鍵的參考寫完之後：ON（DELETE、UPDATE）、NOT（FOR REPLICATION）與其他資料行條件約束。
        new("ReferencesTail", "CREATE TABLE t (a int REFERENCES u (a) "),

        // 模組的 WITH 選項寫完之後是本體的 AS；函式參數清單之後的 RETURNS 不是關鍵字，由子句片語給。
        new("ModuleHeader",
            "CREATE VIEW v WITH SCHEMABINDING ",
            "CREATE PROCEDURE p WITH RECOMPILE ",
            "CREATE FUNCTION f () RETURNS int WITH SCHEMABINDING "),
        new("FunctionReturns", "CREATE FUNCTION f () "),

        // WITH RESULT SETS 的兩層括號：外層每一項是一組資料行定義或 AS OBJECT／TYPE／FOR XML，
        // 內層每一項是「名稱 型別 [COLLATE] [NULL | NOT NULL]」，名稱之後的型別走型別清單。
        new("ResultSetList", "EXEC p WITH RESULT SETS (", "EXEC p WITH RESULT SETS ((a int), "),
        new("ResultSetColumn", "EXEC p WITH RESULT SETS ((", "EXEC p WITH RESULT SETS ((a int, "),
        new("ResultSetColumnTail",
            "EXEC p WITH RESULT SETS ((a int ",
            "EXEC p WITH RESULT SETS ((a varchar(10) COLLATE Latin1_General_CI_AS "),

        // 清單片語（,*）宣告的選項清單：所有敘述共用這一格，選項由片語的標頭分。樣板要有對應的清單片語才判得出來。
        // 每種清單都放：這一格的關鍵字（RESTORE 的 FILE）要從它自己那句撈；選項多半不是關鍵字，由片語給。
        new("OptionItem",
            "ALTER USER u WITH ", "ALTER USER u WITH NAME = n, ",
            "EXEC p WITH ", "EXEC p WITH RECOMPILE, ",
            "RAISERROR ('x', 16, 1) WITH ", "RAISERROR ('x', 16, 1) WITH NOWAIT, ",
            "DBCC CHECKDB WITH ", "DBCC CHECKDB WITH NO_INFOMSGS, ",
            "BACKUP DATABASE d TO DISK = 'x' WITH ", "BACKUP DATABASE d TO DISK = 'x' WITH COMPRESSION, ",
            "RESTORE DATABASE d FROM DISK = 'x' WITH ", "RESTORE DATABASE d FROM DISK = 'x' WITH REPLACE, ",
            "CREATE TRIGGER tr ON DATABASE FOR ", "CREATE TRIGGER tr ON DATABASE FOR CREATE_TABLE, ",
            "CREATE STATISTICS s ON t (a) WITH ", "CREATE STATISTICS s ON t (a) WITH FULLSCAN, ",
            "CREATE QUEUE t WITH ", "CREATE QUEUE t WITH STATUS = ON, ",
            "ALTER QUEUE t WITH ", "ALTER QUEUE t WITH STATUS = ON, "),

        // GROUP BY 的欄位之後：HAVING、ORDER 與 WITH ROLLUP，不接 ASC、DESC。
        new("GroupByTail", "SELECT * FROM t GROUP BY a "),

        // 欄位本身的位置。兩個都要：ORDER BY 接得了 ASC／DESC 以外的運算式關鍵字
        // （CASE、CONVERT、IIF），GROUP BY 接得了 ROLLUP、CUBE、GROUPING SETS。
        new("OrderByColumn", "SELECT * FROM t ORDER BY ", "SELECT * FROM t GROUP BY "),

        new("ByAnchor", "SELECT * FROM t ORDER ", "SELECT * FROM t GROUP "),

        // ALTER TABLE 的三個位置。少了它們，這三處一律回 Any，於是整份關鍵字目錄
        // 與所有片段全部進場——而成熟的補全工具在 ADD 之後只給九個字。
        new("AlterTableAction", "ALTER TABLE t "),
        new("AlterTableAdd", "ALTER TABLE t ADD ", "ALTER TABLE t ADD a int, "),
        new("AlterTableColumn", "ALTER TABLE t ALTER COLUMN ", "ALTER TABLE t DROP COLUMN "),
        new("DdlObject", "CREATE ", "ALTER ", "DROP "),

        // WHEN 的條件寫到哪裡都是同一個位置：WHEN a 之後是 IN、IS、LIKE、BETWEEN，
        // WHEN a = 1 之後才是 THEN、AND、OR——與 ExpressionTail 的兩條同一個道理，
        // 只是這裡不接 WHERE、GROUP 那些子句。
        new("CaseArm", "SELECT CASE WHEN a ", "SELECT CASE WHEN a = 1 ", "SELECT CASE a WHEN 1 "),
        new("CaseBody", "SELECT CASE WHEN a = 1 THEN 1 "),

        // 資料行定義清單的每一項開頭：新資料行名稱，或 CONSTRAINT、PRIMARY KEY 這些字。
        // DECLARE @t TABLE (、RETURNS @t TABLE ( 得到的字與 CREATE TABLE 相同，不必另列。
        new("ColumnDefinition", "CREATE TABLE t (", "CREATE TABLE t (a int, "),

        // 型別或計算資料行的運算式寫完之後：NOT NULL、IDENTITY、條件約束；HIDDEN、SPARSE、PERSISTED 由子句片語給。
        // 只配某些寫法的字各探自己的樣板，由 DdlPhrases 的證據補進同一格（計算資料行、varbinary(max)、IDENTITY (…)、OPENJSON、
        // ALTER COLUMN、條件約束選項、xml、字元型別）。ALTER COLUMN 寫到型別就完整，這裡多出整份語句開頭的字；
        // 不影響清單：這一格一定比對得到位置片語，關鍵字只由片語給。
        new("ColumnDefinitionTail",
            "CREATE TABLE t (a int ", "CREATE TABLE t (a AS b ", "CREATE TABLE t (a varbinary(max) ",
            "CREATE TABLE t (a int IDENTITY(1, 1) ", "SELECT * FROM OPENJSON(@j) WITH (a int ",
            "ALTER TABLE t ALTER COLUMN a int ", "CREATE TABLE t (a int UNIQUE WITH FILLFACTOR = 80 ", "CREATE TABLE t (a xml ",
            "CREATE TABLE t (a nchar "),
        new("BlockStart", "BEGIN ", "BEGIN TRY SELECT 1 END TRY BEGIN "),

        // 區塊寫完：下一句，以及 IF 的 ELSE、TRY／CATCH 區塊的 END TRY、END CATCH。
        new("BlockEnd",
            "BEGIN SELECT 1 END ", "IF 1 = 1 BEGIN SELECT 1 END ", "BEGIN TRY SELECT 1 END ",
            "BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT 1 END "),

        // IF 的主體只有一句，那一句寫完：下一句，以及 ELSE。主體用寫完就沒有續寫子句的一句，
        // 這一格才只多出 ELSE，不會把 SELECT 之後的 FROM、INTO 也掛上來。
        new("IfBodyEnd", "IF 1 = 1 SET NOCOUNT ON "),

        // 游標的選項不是關鍵字（LOCAL、FAST_FORWARD 是識別字），由子句片語給；這裡只撈得到 FOR。
        new("CursorOption", "DECLARE c CURSOR ", "DECLARE c CURSOR LOCAL FAST_FORWARD "),

        // 序列的選項（START WITH、INCREMENT BY、NO CYCLE）同樣不是關鍵字，也不以逗號分隔；這裡撈得到 AS、NO。
        new("SequenceOption", "CREATE SEQUENCE t ", "CREATE SEQUENCE t START WITH 1 "),

        // 下面兩個同一個道理：AFTER、INSTEAD、MATCHED 都不是關鍵字，由子句片語給。
        new("TriggerHeader", "CREATE TRIGGER tr ON t ", "CREATE TRIGGER tr ON t WITH ENCRYPTION "),
        new("MergeWhen", "MERGE t USING s ON 1 = 1 WHEN ", "MERGE t USING s ON 1 = 1 WHEN MATCHED THEN DELETE WHEN "),

        // MERGE 的 THEN 之後是動作；ON 條件或一個動作寫完之後是下一個 WHEN、OUTPUT、OPTION。
        new("MergeAction",
            "MERGE t USING s ON 1 = 1 WHEN MATCHED THEN ",
            "MERGE t USING s ON 1 = 1 WHEN NOT MATCHED THEN "),
        new("MergeClause", "MERGE t USING s ON 1 = 1 WHEN MATCHED THEN DELETE "),

        // 模組的 WITH 選項（ENCRYPTION、SCHEMABINDING、RECOMPILE）同樣不是關鍵字，四種模組各自一格。
        // 不寫成清單片語：選項寫完之後要回報標頭的尾端（AS、FOR），EXECUTE AS 這種多字選項也以位置為鍵。
        new("ProcedureOption", "CREATE PROCEDURE p WITH ", "CREATE PROCEDURE p WITH ENCRYPTION, "),
        new("FunctionOption",
            "CREATE FUNCTION f () RETURNS int WITH ",
            "CREATE FUNCTION f () RETURNS int WITH SCHEMABINDING, "),
        new("ViewOption", "CREATE VIEW v WITH ", "CREATE VIEW v WITH SCHEMABINDING, "),
        new("TriggerOption", "CREATE TRIGGER tr ON t WITH ", "CREATE TRIGGER tr ON t WITH ENCRYPTION, "),

        // 觸發程序的事件清單：AFTER|FOR|INSTEAD OF 與逗號之後是事件，事件寫完之後是 AS、WITH APPEND、NOT FOR REPLICATION。
        new("TriggerEvent",
            "CREATE TRIGGER tr ON t AFTER ",
            "CREATE TRIGGER tr ON t INSTEAD OF ",
            "CREATE TRIGGER tr ON t AFTER INSERT, "),
        new("TriggerEventEnd",
            "CREATE TRIGGER tr ON t AFTER INSERT ",
            "CREATE TRIGGER tr ON DATABASE FOR CREATE_TABLE "),
        new("SetTarget", "SET "),

        // SET 的選項名稱寫完之後的 ON／OFF。各選項自己的值（隔離等級、STATISTICS IO…）
        // 由第四階段的子句片語逐一探測，這裡只是片語比對不上時（選項清單的逗號之後）的退路。
        new("SetOptionValue", "SET NOCOUNT ", "SET IDENTITY_INSERT t "),
        new("InsertTarget", "INSERT "),
    ];
}
