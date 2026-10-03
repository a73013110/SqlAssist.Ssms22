# 子句邊界與不開清單

本頁包含往回判斷子句關鍵字時要認得的結構與語句界線，以及哪些位置不開清單或預設不選。
清單本身怎麼排名與觸發見[補全](completion.md)，關鍵字目錄見[關鍵字](completion-keywords.md)。

## 往回找子句關鍵字時要認得的結構

- **括號群組是一個運算元**，往回走時整組跳過；走進
  `FROM (SELECT … ON a = b) d ` 撈到內層的 `ON` 就打不出 `WHERE`。
  配對不起來時放行成 `Any`。
- **寫完的 `CASE … END` 也是運算元**，後面照外層位置。還沒寫完的 CASE 是游標
  那一層：`CASE WHEN a = 1 THEN b ` 之後接 `WHEN`、`ELSE`、`END`。穿過函式引數或 `IN` 的括號就不是了，
  述詞位置上的括號（`WHEN a = 1 AND (b = 2 OR c `）仍是同一個條件，接 `IS`、`THEN`。
- **`IIF(` 的第一個引數是述詞**：左括號就是它的 `WHERE`，`IIF(Fee > 2 ` 接 `AND`、`OR`，`IIF(@a ` 接 `IS`；
  逗號之後的引數是一般的值。穿出去借外層的話只剩選取清單尾端。
- **逗號之後回到清單的起點**：`SELECT a, ` 與 `SELECT ` 同一個位置，判成尾端的話 `CASE` 不見。沒關上的左括號也是起點（引數、`VALUES` 的一列），
  不借外層：`VALUES (1, ` 才列得出 `NULL`。
- **TOP 子句不是選取清單的一項**：`SELECT TOP 10 ` 之後仍是起點，另接 `PERCENT`、`WITH TIES`。
- **`ON` 的述詞寫完之後是兩個位置的聯集**：述詞尾端（`AND`、`OR`）與資料來源尾端
  （`WHERE`、`JOIN`）。
- **`SET` 子句寫完之後也是**：`UPDATE t SET a = 1 ` 另接 `WHERE`、`FROM`、`OUTPUT`。
  DML 的 `OUTPUT` 一項寫完接 `AS`、`INTO`。
- **其餘的 `SET` 帶出選項**（含 `ALTER DATABASE x SET`）。名稱寫完
  （`SET NOCOUNT `）只列 `ON`、`OFF` 這類值；停在關鍵字上是還沒寫完（`SET IDENTITY_INSERT `
  要資料表）。**值寫完這一句就結束**；否則 `SET ANSI_NULLS ON⏎G` 的
  `ON` 被當成 JOIN 的，`GO` 變成 `GROUPING`。識別字的值（`SET DATEFORMAT dmy`）
  與名稱分不開，換行才補語句開頭。
- **`NOT` 也是聯集**：`WHERE NOT ` 開一個述詞，`a NOT ` 之後接 `IN`、`LIKE`。
- **`IF`、`WHILE` 是錨點**：條件寫完是主體的開頭，也接 `AND`、`OR`；括號沒關上時只算條件。
  IF 的單句主體寫完另接 `ELSE`（`IfBodyEnd`）；以分號結束也一樣，分號只說前一句寫完了
  （`IF @a = 1 PRINT 'x'; ELSE`、`BEGIN … END; ELSE`）。
- **區塊邊界之後是下一句**：`BEGIN TRY`、`END CATCH`、IF 的 `ELSE`。`BEGIN … END` 的 END
  另接 `ELSE`、`TRY`、`CATCH`（`BlockEnd`）；CASE 的 ELSE 與 END 不算。

## 名稱位置三分類

分析器替游標那一格分類（`SqlCompletionSlot`）；開不開、選不選只看
`SqlCompletionPolicy` 一條規則。開的兩類還要候選集合封閉或前綴達到觸發字元數。

| 分類 | 意思 | 例子 | 清單 |
|---|---|---|---|
| `Inert` | 不可補 | 字串、註解、`10`、`1.` | 不開 |
| `Name` | 一定是新名字 | `AS `、`DECLARE @`、`CREATE PROCEDURE dbo.` | 不開 |
| `MaybeName` | 名字或關鍵字都可能 | `FROM dbo.T `、`SELECT PublCode `、`CREATE PROCEDURE ` | 開，軟選 |
| `Grammar` | 其餘 | `SELECT `、`WHERE a = ` | 開，打了字才硬選 |

以數字開頭的詞元是數值常值，歸 `Inert`：`Fine - 10` 的 `10` 模糊比對會對到 `LOG10`，Enter 就換成函式。
點號前那一段以數字開頭也算（`1.`，否則平台以限定字 `1` 開清單），方括號裡的不算（`[192.0.2.10].` 是連結伺服器）。

`MaybeName` 的 `FROM dbo.T W` 可能是別名也可能是打到一半的 `WHERE`，軟選讓 Enter 保住別名，
代價是 `FR`＋Enter 不再補成 `FROM`。它寫得出清單外的新名字，不封閉。

- **`Name`**：`AS ` 之後的別名、`DECLARE @`（見[變數](completion-variables.md)）、`CREATE <種類> ` 列不出東西的那一段、`WITH ` 與 `WITH a AS (…), ` 的 CTE 名、`SELECT … INTO ` 的新資料表
  （`INSERT INTO `、`MERGE INTO ` 要既有資料表，是 `Grammar`）、`RESULT SETS ((` 的資料行名稱。
- **`MaybeName`**：同一行沒有 AS 的別名、文法強制別名的括號之後（衍生資料表、`PIVOT (…) `、
  `UNPIVOT (…) `）、`CREATE <種類> ` 列得出東西的那一段、資料行定義的起點（`CREATE TABLE t (`、逗號之後、
  `DECLARE @t TABLE (`）、`ALTER TABLE t ADD `——新資料行名稱或 `CONSTRAINT` 都對。

括號是什麼由**前面**那個字決定：接在 `FROM`、`JOIN`、`APPLY`、`USING` 後面的是衍生資料表，
接在 `IN`、`EXISTS`、`=` 後面的是運算式；`FROM (t1 JOIN t2 ON …) ` 是括號包起來的聯結、
名稱後的 `WITH (NOLOCK)` 是提示，都不接別名。衍生資料表判成 `Name` 的話清單不開，`AS` 打不出來。
`AS` 也看前面：一項剛寫完、還沒有別名時才是別名，其餘照常——`CREATE VIEW v AS ` 的主體、`EXECUTE AS`、`FOR SYSTEM_TIME AS`（接 `OF`）。
`CAST(x AS ` 由[型別的位置](completion-builtins.md#資料型別)先接走。

### 別名規則

資料來源與選取清單共用一條：**同一行、一項剛寫完、還沒有別名**就是 `MaybeName`。

- 一項：資料來源是名稱或資料表值函式呼叫，連同後綴 `FOR SYSTEM_TIME …` 與函式的
  `WITH (…)` 資料行結構描述（`OPENJSON(@j) WITH (a int) `），前面直接是 `FROM`、`JOIN`、
  `APPLY`、`USING`、`MERGE [INTO]` 或 FROM 清單的逗號；選取清單是一整個運算式，
  往回到 `SELECT`、逗號或 TOP 子句。
- DELETE 自己的 FROM 帶出動詞的目標、不接資料來源的 FROM 帶出游標或裝置，都不接別名
  （`Grammar`）：`DELETE [TOP (5)] FROM t `、`FETCH NEXT FROM c `。`DELETE a FROM t ` 照常。
- 項目結尾是識別字、變數、`)`、常值或 `CASE … END` 的 `END`；`*` 後面不接別名。
- 還沒有別名：最後一個運算元前面不緊鄰另一個運算元或 `AS`。
- 同一行：前一個詞元結尾到游標之間沒有換行（註解前的也算），
  這是分開別名與 `WHE` 的唯一線索。

### CREATE 的名稱格

名稱只有最後一段是新的：**列得出東西的那一段是 `MaybeName`，列不出才是 `Name`**（`CreatedNameSlot`）。
列得出的是既有物件（`CREATE OR ALTER `）、同一格的字（`CREATE DATABASE SCOPED`），或還沒寫限定字時的
結構描述（種類由[產生器](phrase-generator.md)探出）。
其餘是 `Name`：`CREATE PROCEDURE dbo.`、`CREATE INDEX `。

取捨：不列資料庫（跨庫建立少見）。打了字才開，否則片段 `cp` 名稱欄位的 Tab 把 `dbo` 提交進
`dbo.ProcedureName`；代價是 `CREATE OR ALTER PROCEDURE ` 也不再空前綴就開。

## 語句的界線

錨點只在游標所在的那一句裡找，自己沒有錨點的一句（`EXEC`、`PRINT`）是 `Any`：
借上一句的話，`WHERE b = 1⏎EXEC p @x ` 打不出 `OUTPUT`。

**語句開頭**是能開始一句的關鍵字，而且前一格是界線。這種字也寫在句中（`WITH (NOLOCK)`、
`DROP TABLE IF`、`THEN UPDATE`），分開它們的是前一格：

- **明確的**：前一格含語句開頭、區塊開頭或區塊的 END——`;`、GO、`BEGIN`、`ELSE`、模組標頭的
  `AS`、IF 的條件、SET 選項的值、寫完一整句的字（`BREAK`）。
- **隱含的**：沒有分號時換行就是界線。`WHERE a = 1⏎` 之後是 `AND` 還是下一句的 `SELECT`，詞元分不出來，
  所以**子句已到尾端又換了行**就聯集 `StatementStart`（尾端見 `StatementEndPositions`，含選取清單：
  `SELECT dbo.fn_Fee('')` 不需要 `FROM`）；前一句判不出位置而寫完一個運算元也一樣。同一行、括號沒關、
  名字那一格前面不補。剖析器不看換行：前一句的[片語](completion-phrases.md)確定接得上的字仍屬於前一句
  （`OFFSET 0 ROWS⏎FETCH`、`ALTER DATABASE d⏎SET`）。
- `WITH` 只認明確的：CTE 前一句必須以分號結束。

子句屬於哪個**動詞**另問往回第一個能開始一句的字（`FindVerb`，`UPDATE t⏎SET` 的 SET 屬於 UPDATE）；
權限清單的一項（`REVOKE SELECT`）與 `WITH` 不算，`IF UPDATE(a)` 是函式，`CASE … END` 整組跳過。

- **FROM 只在動詞是 SELECT、UPDATE、DELETE 時接資料來源，INTO 只有 FETCH 的不接**：`FETCH NEXT FROM c ` 接 `INTO`
  （`FetchTail`），`RESTORE`、`REVOKE`、`BULK INSERT` 的 FROM 是 `Any`。位置、目標與範圍分析
  共用 `IntroducesDataSource`，分岔時 `DISK` 被收成一張表。
