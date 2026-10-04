# 名稱位置

本頁包含游標那一格是不是名稱：開不開清單、軟硬選、別名與新名字，以及哪些關鍵字位置不列資料庫物件；
子句回溯與語句界線見[子句邊界](completion-boundaries.md)，位置旗標的產生見[關鍵字](completion-keywords.md)。

## 三分類

分析器替游標那一格分類（`SqlCompletionSlot`）；開不開、選不選只看
`SqlCompletionPolicy` 一條規則。開的兩類還要候選集合封閉或前綴達到觸發字元數。

| 分類 | 意思 | 清單 |
|---|---|---|
| `Inert` | 不可補：字串、註解、數值常值 | 不開 |
| `Name` | 一定是新名字 | 不開 |
| `MaybeName` | 名字或關鍵字都可能 | 開，軟選 |
| `Grammar` | 其餘（`SELECT `、`WHERE a = `） | 開，打了字才硬選 |

數字開頭的詞元是常值（`Inert`），否則 `Fine - 10` 模糊比對到 `LOG10`，Enter 就換成函式。點號前那段
數字開頭也算（`1.`，否則平台以限定字 `1` 開清單），方括號裡的不算（`[192.0.2.10].` 是連結伺服器）。

`MaybeName` 的 `FROM dbo.T W` 可能是別名或打到一半的 `WHERE`，軟選讓 Enter 保住別名，
代價是 `FR`＋Enter 不補成 `FROM`。它寫得出清單外的新名字，不封閉。

- **`Name`**：`AS ` 之後的別名、`DECLARE @`、[CREATE 名稱](#create-的名稱格)列不出東西的那一段、`WITH ` 與 `WITH a AS (…), ` 的 CTE 名、`SELECT … INTO ` 的新資料表
  （`INSERT INTO `、`MERGE INTO ` 要既有資料表，是 `Grammar`）、`RESULT SETS ((` 的資料行名稱。
- **`MaybeName`**：同一行沒有 AS 的別名、文法強制別名的括號之後（衍生資料表、`PIVOT (…) `、
  `UNPIVOT (…) `）、CREATE 名稱列得出東西的那一段、資料行定義的起點（`CREATE TABLE t (`、逗號之後、`DECLARE @t TABLE (`）、
  `ALTER TABLE t ADD `——新資料行名稱或 `CONSTRAINT` 都對。

括號與 `AS` 是什麼都由**前面**決定：資料來源位置的括號是衍生資料表，`IN`、`EXISTS`、`=` 後面的是運算式；
括號包起來的聯結（`FROM (t1 JOIN t2 ON …) `）與名稱後的提示（`WITH (NOLOCK)`）不接別名。衍生資料表判成 `Name`
就打不出 `AS`。`AS` 只在一項剛寫完、還沒有別名時是別名，其餘照常（`CREATE VIEW v AS `、`EXECUTE AS`、
`FOR SYSTEM_TIME AS` 接 `OF`）；`CAST(x AS ` 由[型別的位置](completion-builtins.md#資料型別)先接走。

### 別名規則

資料來源與選取清單共用：**同一行、一項剛寫完、還沒有別名**就是 `MaybeName`。

- 一項：資料來源是名稱或資料表值函式呼叫，連同後綴 `FOR PATH`、`FOR SYSTEM_TIME …` 與函式的
  `WITH (…)` 資料行結構描述（`OPENJSON(@j) WITH (a int) `），前面直接是 `FROM`、`JOIN`、
  `APPLY`、`USING`、`MERGE [INTO]` 或 FROM 清單的逗號；選取清單是一整個運算式，
  往回到 `SELECT`、逗號或 TOP 子句。
- 帶出目標、游標或裝置的 FROM 不接別名（`Grammar`）：`DELETE [TOP (5)] FROM t `、`FETCH NEXT FROM c `；
  `DELETE a FROM t ` 照常。
- 項目結尾是識別字、變數、`)`、常值或 `CASE … END` 的 `END`；`*` 後面不接別名。
- 還沒有別名：最後一個運算元前面不緊鄰另一個運算元或 `AS`。
- 同一行：前一個詞元結尾到游標沒有換行（註解前的也算）。

### CREATE 的名稱格

名稱只有最後一段是新的：**列得出東西才是 `MaybeName`，否則 `Name`**（`CreatedNameSlot`）。
列得出的是既有物件（`CREATE OR ALTER `）、同一格的字（`CREATE DATABASE SCOPED`），或還沒寫限定字時的
結構描述；其餘是 `Name`：`CREATE PROCEDURE dbo.`、`CREATE INDEX `。

取捨：不列資料庫（跨庫建立少見）。打了字才開，否則片段 `cp` 名稱欄位的 Tab 把 `dbo` 提交進
`dbo.ProcedureName`；代價是 `CREATE OR ALTER PROCEDURE ` 也不再空前綴就開。

## 不列資料庫物件的位置

關鍵字、內建函式與片段帶著位置旗標，**名稱沒有**（執行期從中繼資料來），
所以反過來列寫不出名稱的位置（`SqlKeywordPositionExtensions.AcceptsNames`），
每一項都要說得出「那裡沒有任何名稱是合法的」：

| 位置 | 那裡只接受 |
|---|---|
| 子句尾端（`GROUP BY a \|`、`WHERE a = 1 \|`、`FROM t a \|`） | 運算子或關鍵字；別名是新名字 |
| `StatementStart`、`BlockStart`、`BlockEnd`、`IfBodyEnd`（`;`、`BEGIN`、區塊的 `END` 之後） | 下一句的關鍵字或 `ELSE` |
| 選項清單與其餘敘述自己的格子（`CursorOption`、`TriggerEvent`、`MergeAction`…，全部見 `NoNamePositions`） | 選項、事件、動作或下一個子句的字 |
| `ByAnchor`（`ORDER \|`、`GROUP \|`） | `BY` |
| `DdlObject`（`CREATE \|`、`ALTER \|`、`DROP \|`） | 物件**種類** |
| `AlterTableAction`、`AlterTableAdd`、`ColumnDefinition` | 動作、條件約束關鍵字，或新資料行名稱 |
| `SetOptionValue`（`SET NOCOUNT \|`） | `ON`、`OFF`（`SET IDENTITY_INSERT \|` 要資料表，不在此） |

子句尾端換行後補上的語句開頭照樣不接名稱：`FROM t a⏎CREA` 的欄位屬於上一句，
省略 EXEC 的程序呼叫又只在**批次第一句**合法（文件開頭或 `GO` 之後，`StartsBatch`）。
那裡只放行程序。

刻意不在裡面的：`InsertTarget`（`INSERT dbo.Loan VALUES (…)` 合法，`INTO` 可省略）、
`SetTarget`（`SET |` 與 `UPDATE t SET |` 同一個位置，後者要資料行）、`CaseArm`、`CaseBody`
（CASE 的各段是運算式，欄位與函式都對）。

判斷比的是「位置裡還有沒有別的位元」而不是位元交集：判不出位置時回傳的 `Any`
含著上表每一個旗標，用交集的話 fail-open 會變成 fail-closed，**每一個**位置的資料庫
物件都會消失。
