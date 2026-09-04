# ON 與 MERGE 的補全上下文

## `ON` 後面是資料表還是述詞

`ON` 在 T-SQL 裡是兩件完全不同的事，而分得開的線索在它**前面**：

```text
CREATE INDEX ix ON |          → 資料表；ON 前面是「名稱＋INDEX」
CREATE TRIGGER tr ON |        → 同上，TRIGGER
DROP INDEX ix ON |            → 同上
ALTER INDEX ALL ON |          → 同上（ALL 是關鍵字，但那一格仍然是索引的名稱）
CREATE STATISTICS st ON |     → 同上，STATISTICS
JOIN b ON |、MERGE … ON |     → 述詞；ON 前面是別名或 AS
GRANT SELECT ON |             → 述詞那一邊；ON 前面是關鍵字，不是名稱單位
CREATE INDEX ix ON t (a) ON | → 檔案群組；ON 前面是右括號
```

判斷刻意**只看 `ON` 前面那兩個名稱單位**，不往回走到敘述開頭：
「名稱之後是 `INDEX`／`STATISTICS`／`TRIGGER`」這個形狀只有 DDL 寫得出來，
而往回走要多認一整套邊界，還會把最後那個檔案群組的 `ON` 一起收進來。
`CREATE`／`ALTER`／`DROP` 三個動詞不必比：名稱後面接 `ON` 的物件只有那三種，
比不比對得到同一個答案，而漏掉 `CREATE OR ALTER TRIGGER` 的症狀是那裡安靜地沒有清單。

這份判斷有**兩個**呼叫端，共用 `Core/Parsing/SqlDdlTarget`：建議目標（列不列資料表）
與 `SqlScopeAnalyzer`（那張表算不算資料來源）。各寫一份的症狀是清單列得出資料表、
欄位卻一個都沒有——而那正是修正前的樣子：`cix` 的資料表格從來沒有清單，
資料行格列出來的是整個資料庫的資料表與預存程序。

反方向比正方向重要：把 JOIN 條件誤判成資料來源的話，`ON b.|` 會退回
「`b` 是結構描述」的解讀而完全列不出欄位，那是每天都會走到的路徑。
兩邊都釘在 `SqlDdlTargetTests`。

## MERGE 的動作子句

`WHEN MATCHED THEN UPDATE SET …`、`WHEN NOT MATCHED THEN INSERT …` 裡的
`UPDATE`／`INSERT`／`DELETE` 屬於同一個 MERGE，不是新敘述的開頭
（`SqlScopeAnalyzer.IsMergeAction`）。把它們當成邊界的話，游標一進到 `WHEN` 之後，
`target` 與 `source` 兩個別名就全部解析不出來——症狀是 `target.` 與 `source.`
都不再列欄位，而 `INSERT (` 連一個候選都沒有。

認的是**前一個詞元是不是 `THEN`**，不是「這份指令碼裡有沒有 MERGE」：一個 MERGE
之後接著獨立的 `UPDATE`，那個 `UPDATE` 仍然必須切斷範圍。T-SQL 裡 `THEN` 只出現在
CASE 與 MERGE，而 CASE 的 `THEN` 後面是運算式，不會是這三個關鍵字。

`INSERT (` 括號裡文法上只該有 target 的欄位，但範圍解析給的是整個 MERGE 的兩張表。
收斂成一張要另外記住「這個括號屬於 INSERT 子句」；多幾個選不中的名稱是多按幾下，
兩張表都不列的話那一格就完全沒有補字。
| `sys.`、`INFORMATION_SCHEMA.` | 目錄檢視、DMV 與系統程序 | 插入名稱 |
| `u.`（`u` 是敘述中的別名） | 該資料表的欄位 | 插入欄位名稱 |

`FROM`、`JOIN` 之後除了資料庫裡的資料表與檢視，還會列出**這份指令碼自己宣告的
資料來源**：CTE 與暫存資料表。中繼資料只看得到目前連線資料庫的 `sys.objects`，
CTE 只存在於指令碼裡，暫存資料表在 tempdb 裡，兩者一個都不在那份清單上——
而使用者會去 `FROM ` 後面補字，正是因為那個名稱是他剛取的、還沒背起來，
所以它們排在資料庫的資料表之前。

暫存資料表不分辨是哪一句建立的：井號開頭的識別字在 T-SQL 裡只有這一種意思，
而 `CREATE TABLE`、`SELECT INTO`、`INSERT INTO` 各認一次的話，漏掉的那一種寫法
就會安靜地少一個名稱。
