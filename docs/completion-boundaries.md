# 子句邊界與不開清單

本頁包含往回判斷子句關鍵字時要認得的結構，以及哪些位置不開清單或預設不選。
清單本身怎麼排名與觸發見[補全](completion.md)，關鍵字目錄見[關鍵字](completion-keywords.md)。

## 往回找子句關鍵字時要認得的結構

「最近的子句關鍵字」不是往回數詞元就找得到的：

| 寫法 | 只數詞元會判成 | 症狀 |
|---|---|---|
| `FROM (SELECT … ON a = b) d ` | 走進子查詢，撈到內層的 `ON` | 打不出 `WHERE` |
| `SELECT a, ` | 選取清單的尾端 | 列出 `FROM`、`INTO`，`CASE` 反而不見 |
| `JOIN b ON b.x = a.x ` | 只有述詞的尾端 | 打不出 `WHERE`、`INNER` |

- **括號群組是一個運算元**，往回走時整組跳過；裡面的子句屬於它自己。
  跳過的括號兩兩不重疊，整趟仍是線性的；配對不起來時放行成 `Any`，不猜。
- **寫完的 `CASE … END` 也是運算元**，後面照外層位置。還沒寫完的 CASE 是游標
  所在的那一層：`CASE WHEN a = 1 THEN b ` 之後接 `WHEN`、`ELSE`、`END`，不是外層尾端。
- **逗號代表清單再來一項**，位置回到清單的**起點**：`SELECT a, ` 與 `SELECT ` 同一個位置。
- **TOP 子句不是選取清單的一項**：`SELECT TOP 10 `、`TOP (10) `、`TOP 10 PERCENT `
  之後仍是起點，另接 `PERCENT`、`WITH TIES`。當成一項會列出 `FROM`，還被當成別名位置。
- **`ON` 的述詞寫完之後是兩個位置的聯集**：述詞尾端（`AND`、`OR`）與資料來源尾端
  （`WHERE`、`JOIN`、`GROUP`）。文法允許兩個就報兩個。
- **`SET` 子句寫完之後也是**：`UPDATE t SET a = 1 ` 之後接得了 `WHERE`、`FROM`、`OUTPUT`，
  那組字掛在資料來源尾端。
- **其餘的 `SET` 帶出選項**（動詞不是 `UPDATE`，含 `ALTER DATABASE x SET`）。名稱寫完
  （`SET NOCOUNT `）只列 `ON`、`OFF` 這類值；停在關鍵字上是還沒寫完（`SET IDENTITY_INSERT `
  要資料表）。**值寫完這一句就結束**，同一行也是語句開頭；否則 `SET ANSI_NULLS ON⏎G` 的
  `ON` 被當成 JOIN 的，Enter 把 `GO` 換成 `GROUPING`。識別字的值（`SET DATEFORMAT dmy`）
  與名稱分不開，換行才補語句開頭。
- **`NOT` 也是聯集**：`WHERE NOT ` 開一個述詞，`a.Big5Code NOT ` 之後接 `IN`、`LIKE`。

## 名稱位置三分類

分析器在同一趟反向走訪裡替游標那一格分類（`SqlCompletionSlot`）；開不開、選不選只看
`SqlCompletionPolicy` 一條規則。開的兩類還要有限定字、目標已收斂，或前綴達到觸發字元數。

| 分類 | 意思 | 例子 | 清單 |
|---|---|---|---|
| `Inert` | 不可補 | 字串、註解、`10`、`1.` | 不開 |
| `Name` | 一定是新名字 | `AS `、`DECLARE @`、`CREATE PROCEDURE ` | 不開 |
| `MaybeName` | 名字或關鍵字都可能 | `FROM dbo.T `、`SELECT PublCode ` | 開，軟選 |
| `Grammar` | 其餘 | `SELECT `、`WHERE a = ` | 開，硬選 |

`Name` 的清單沒有一項會對，彈出來只會讓 Enter 把剛打的 `a` 換成 `ALTER PROCEDURE`。
`MaybeName` 的 `FROM dbo.T W` 可能是別名也可能是打到一半的 `WHERE`：軟選時只有 Tab 提交，
Enter 照常換行保住別名，按 ↓ 轉成硬選。代價是 `FR`＋Enter 不再補成 `FROM`。

- **`Name`**：`AS ` 之後的別名、文法強制別名的括號之後（衍生資料表、`PIVOT (…) `、
  `UNPIVOT (…) `）、`DECLARE @`（見[變數](completion-variables.md)）、`CREATE <種類> ` 與
  `CREATE INDEX ` 的新物件、敘述開頭 `WITH ` 與 `WITH a AS (…), ` 的 CTE 名、`SELECT … INTO ` 的新資料表
  （`INSERT INTO `、`MERGE INTO ` 要既有資料表，是 `Grammar`）。
- **`MaybeName`**：同一行沒有 AS 的別名、`CREATE OR ALTER <種類> `（常是既有物件；
  `ALTER <種類> ` 是 `Grammar`）、資料行定義的起點（`CREATE TABLE t (`、逗號之後、
  `DECLARE @t TABLE (`）、`ALTER TABLE t ADD `——新資料行名稱或 `CONSTRAINT` 都對。

括號是什麼由**前面**那個字決定：接在 `FROM`、`JOIN`、`APPLY`、`USING` 後面的是衍生資料表，
接在 `IN`、`EXISTS`、`=` 後面的是運算式；`FROM (t1 JOIN t2 ON …) ` 是括號包起來的聯結、
名稱後的 `WITH (NOLOCK)` 是提示，都不接別名。`AS` 也看前面：一項剛寫完、還沒有別名時才是
別名，其餘照常——`CREATE VIEW v AS ` 的主體、`EXECUTE AS`、`FOR SYSTEM_TIME AS`（接 `OF`）。
`CAST(x AS ` 由「型別的位置」先接走，見[資料型別](completion-builtins.md#資料型別)。

### 別名規則

資料來源與選取清單共用一條：**同一行、一項剛寫完、還沒有別名**就是 `MaybeName`。

- 一項：資料來源是名稱或資料表值函式呼叫，連同後綴 `FOR SYSTEM_TIME …` 與函式的
  `WITH (…)` 資料行結構描述（`OPENJSON(@j) WITH (a int) `），前面直接是 `FROM`、`JOIN`、
  `APPLY`、`USING`、`MERGE [INTO]` 或 FROM 清單的逗號；選取清單是一整個運算式，
  往回到 `SELECT`、逗號或 TOP 子句。
- DELETE 與 FETCH 自己的 FROM 帶出動詞的目標，文法不接別名（`Grammar`）：
  `DELETE [TOP (5)] FROM t `、`FETCH NEXT FROM c `。`DELETE a FROM t ` 前面已有目標，照常。
- 項目結尾是識別字、變數、`)`、常值或 `CASE … END` 的 `END`；`*` 後面不接別名。
- 還沒有別名：最後一個運算元前面不緊鄰另一個運算元或 `AS`。
- 同一行：前一個詞元結尾到游標之間沒有換行（註解前的也算）。別名一定寫在同一行，
  子句與下一句幾乎總是換行——這是分開別名與 `WHE` 的唯一線索。

```text
FROM dbo.PUBLISHER |、SELECT a + b | → MaybeName
FROM CTE_TEST a |、SELECT * |        → Grammar
FROM dbo.PUBLISHER ⏎ |               → 換行了，Grammar
```

## 沒有分號時，換行就是敘述邊界

省略分號時，`WHERE a = 1` 之後換行寫 `SELECT` 或 `AND`，詞元串流分不出差別。
只保留上一句子句尾端會把下一句的語句級片段（`ssf`…）濾光。因此**子句已到尾端，
而且游標換了行**，就補上 `StatementStart`。

```text
SELECT * FROM dbo.Loan WHERE ReaderId = 1 ⏎ | → ssf、SELECT、AND 都在
SELECT * FROM dbo.Loan WHERE ReaderId = 1 |  → 同一行，不補
SELECT dbo.fn_Fee(1 ⏎ |                      → 括號未關，不補
```

補的是**旗標聯集**，不換掉原位置：`FROM`、`AND`、`ORDER` 等續寫的字一個都不少。

認的尾端：選取清單、資料來源、述詞、`ORDER BY`／`GROUP BY` 欄位之後，以及 SET 選項名稱之後
（識別字的值）。選取清單也算：`SELECT dbo.fn_Fee('')` 不需要 `FROM`，排除它的代價是
那一句之後打不出任何語句級片段。名字那一格前面的換行不補：名字本身還沒寫。

換行的判準與別名規則的「同一行」相同（LF、CRLF、CR 都認），字串或加引號名稱內的換行
不算。函式引數、子查詢與 CTE 的括號還沒關時不補（`SqlTokenNavigator`）。

## 數值常值不開清單

`UPDATE t SET Fine = Fine - 10` 打到 `10` 時，運算子之後是 `Any`，整個目錄進場，
模糊比對把 `10` 對到 `LOG10`，順手按 Enter 數字就變成函式名稱。

T-SQL 的一般識別字不能以數字開頭，所以以數字開頭的詞元必然是數值常值，歸 `Inert`。
比的是第一個字元，不是「含不含數字」：`Cat_BookCopy2` 很常見。判斷在
`SqlCompletionContextAnalyzer.IsInNumericLiteral`，點號前那一段以數字開頭也算（`1.`、`12.`）：
文字上與 `dbo.` 一樣是限定字加點號，平台在點號自己觸發時會以限定字 `1` 開清單。
方括號裡的不算（`[192.0.2.10].` 是連結伺服器）。

變數後的點號只有資料表變數算限定字：`@rows.` 列它的資料行（提交時改寫成 `[@rows].`，
見[插入文字](completion-insertion.md#欄位的限定字另有一條)），純量變數的 `@x.value(`
是 xml 方法，歸 `Inert`。
