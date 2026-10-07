# 子句邊界與語句界線

本頁包含往回判斷子句關鍵字時要認得的結構與語句界線；哪一格是名稱、不開清單或預設不選見
[名稱位置](completion-name-slots.md)，關鍵字目錄見[關鍵字](completion-keywords.md)。

## 往回找子句關鍵字時要認得的結構

- **括號群組是一個運算元**，往回整組跳過：撈到 `FROM (SELECT … ON a = b) d ` 內層的 `ON` 就沒有 `WHERE`。
  配對不起來就放行成 `Any`。緊接括號的 `UPDATE` 是函式、一起跳過：`IF (UPDATE(a) ` 接 `OR`，不是 DML 的資料來源尾端；
  其餘錨點（`WHERE (`、`FROM (`）接括號仍是錨點。
- **寫完的 `CASE … END` 也是運算元**。沒寫完的 CASE 是游標那一層：`CASE WHEN a = 1 THEN b `
  接 `WHEN`、`ELSE`、`END`；述詞的括號（`WHEN a = 1 AND (b = 2 OR c `）仍是同一個條件，接 `IS`、`THEN`，
  函式引數或 `IN` 的括號不是。
- **`IIF(` 的第一個引數是述詞**：左括號就是它的 `WHERE`，`IIF(Fee > 2 ` 接 `AND`、`OR`，`IIF(@a ` 接 `IS`；
  逗號之後是一般的值，借外層只剩選取清單尾端。
- **逗號之後回到清單的起點**：`SELECT a, ` 與 `SELECT ` 同一個位置，判成尾端就沒有 `CASE`。沒關上的左括號也是起點（引數、`VALUES` 的一列），
  不借外層：`VALUES (1, ` 才列得出 `NULL`。定義清單的逗號之前是上一項（`… CASCADE, CONSTRAINT c ` 不借它的 `ON`）。
- **TOP 子句不是選取清單的一項**：`SELECT TOP 10 ` 仍是起點，另接 `PERCENT`、`WITH TIES`；DML 的回到動詞之後（`MERGE TOP (10) `）。
- **`ON` 的述詞寫完是兩個位置的聯集**：述詞尾端（`AND`、`OR`）與資料來源尾端（`WHERE`、`JOIN`）。
- **`SET` 子句寫完也是**：`UPDATE t SET a = 1 ` 另接 `WHERE`、`FROM`、`OUTPUT`。
  `OUTPUT` 與 `RECEIVE` 的清單是選取清單，一項寫完接 `AS`、`INTO`。
- **其餘的 `SET` 帶出選項**（含 `ALTER DATABASE x SET`）：名稱寫完（`SET NOCOUNT `）只列 `ON`、`OFF`
  這類值，停在關鍵字上是沒寫完（`SET IDENTITY_INSERT ` 要資料表）。**值寫完這一句就結束**，否則
  `SET ANSI_NULLS ON⏎G` 的 `ON` 被當成 JOIN 的，`GO` 變成 `GROUPING`。等號後的 `ON`（`ONLINE = ON`）與 WITH 清單一項第一個字之後的 `ON`（`WITH TRUSTWORTHY ON`）也是值，
  之後判不出位置；識別字的值（`SET DATEFORMAT dmy`）與名稱分不開，換行才補語句開頭。
- **`NOT` 也是聯集**：`WHERE NOT ` 開一個述詞，`a NOT ` 之後接 `IN`、`LIKE`。
- **`IF`、`WHILE` 是錨點**：條件寫完是主體的開頭，也接 `AND`、`OR`；括號沒關上時只算條件。
  IF 的單句主體寫完另接 `ELSE`（`IfBodyEnd`）；分號只說前一句寫完了，一樣接（`IF @a = 1 PRINT 'x'; ELSE`）。
- **區塊邊界之後是下一句**：`BEGIN TRY`、`END CATCH`、IF 的 `ELSE`；`BEGIN … END` 的 END
  另接 `ELSE`、`TRY`、`CATCH`（`BlockEnd`），CASE 的不算。

## 語句的界線

錨點只在游標所在的那一句裡找，自己沒有錨點的一句（`EXEC`、`PRINT`）是 `Any`：
借上一句的話，`WHERE b = 1⏎EXEC p @x ` 打不出 `OUTPUT`。

**語句開頭**是能開始一句、前一格是界線的關鍵字，或語句開頭附加片語的字（`COPY`、`ENABLE`；
`StartsStatementAsPhrase`）：後者不進目錄，否則 `Copy` 資料表會被自動大寫。這種字也寫在句中（`WITH (NOLOCK)`、
`DROP TABLE IF`、`THEN UPDATE`）：

- **明確的**：前一格含語句開頭、區塊開頭或區塊的 END——`;`、GO、`BEGIN`、`ELSE`、模組標頭的
  `AS`、IF 的條件、SET 選項的值、寫完一整句的字（`BREAK`）。
- **隱含的**：沒有分號時換行就是界線，但 `WHERE a = 1⏎` 之後是 `AND` 還是下一句的 `SELECT` 詞元分不出來，
  所以**子句已到尾端又換了行**才聯集 `StatementStart`（尾端見 `StatementEndPositions`，含選取清單：
  `SELECT dbo.fn_Fee('')` 不需要 `FROM`）；前一句判不出位置而寫完一個運算元也算。同一行、括號沒關、
  名字那一格前面不補。前一句[片語](completion-phrases.md)接得上的字仍屬於前一句
  （`OFFSET 0 ROWS⏎FETCH`、`ALTER DATABASE d⏎SET`）。
- `WITH` 只認明確的：CTE 前一句必須以分號結束。

子句屬於哪個**動詞**另問往回第一個能開始一句的字（`FindVerb`：`UPDATE t⏎SET` 屬於 UPDATE）；
權限清單的一項（`REVOKE SELECT`、`GRANT CREATE TABLE`）與 `WITH` 不算，清單項的走訪（`StartsClauseOfItsOwn`）同一條；`IF UPDATE(a)` 是函式，`CASE … END` 整組跳過；
不是關鍵字的語句開頭要真的是一句的開頭才算，否則 `SELECT Copy.CopyNo FROM` 的 FROM 找不到 SELECT。
安全性原則 `AFTER`、`BEFORE` 之後的 `INSERT`、`UPDATE`、`DELETE` 是作業，不是動詞、語句開頭或子句錨點：否則 `AFTER UPDATE` 之後列資料表，換行寫的下一句也認不出開頭。

- **FROM 只在動詞是 SELECT、UPDATE、DELETE 時接資料來源，INTO 只有 FETCH 的不接**：`FETCH NEXT FROM c `
  接 `INTO`（`FetchTail`），`RESTORE`（裝置清單）、`REVOKE`、`BULK INSERT`、`COPY INTO` 的 FROM 不接，簽章有 FROM 的函式括號裡（`TRIM('x' FROM s)`、`{fn EXTRACT(HOUR FROM d)}`）之後是運算式，`IS [NOT] DISTINCT FROM` 是比較運算子、也不是子句錨點。位置、目標與範圍分析共用 `IntroducesDataSource`，分岔時 `DISK` 被收成表。
