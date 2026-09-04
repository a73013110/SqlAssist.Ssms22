# 上下文收斂與自動大寫

游標前方的文字決定清單裡剩下什麼：哪些位置只剩固定那幾個字、哪些位置要把整類項目
拿掉、以及關鍵字在什麼時機自動變成大寫。判斷的出處是
`Core/Completion/SqlCompletionContextAnalyzer`，它讀的是實際文字而不是任何宣告。
清單本身怎麼排名與觸發見 [completion.md](completion.md)。

## 引數與提示的封閉清單

有三個位置除了固定那幾個字以外沒有別的東西是對的，它們與資料型別是同一種判斷、
同一個代價權衡——判定成立時整份清單就換掉，所以只收看得出來的：

```text
SELECT DATEADD(|              → DAY、MONTH、YEAR…（15 個日期部分）
SELECT * FROM dbo.Loan WITH (| → NOLOCK、UPDLOCK、INDEX(…（21 個資料表提示）
SELECT * FROM dbo.Loan OPTION (| → RECOMPILE、MAXDOP、FORCE ORDER…（17 個查詢提示）
```

三種都認得出來，是因為左括號**前面**那個字就把話說完了。CTE 的 `WITH` 不會誤判——
`;WITH c AS (` 的 `WITH` 與左括號之間隔著一個名稱。

日期部分只在**第一個**引數：打過逗號之後那裡要的是數字與日期。提示則是一份清單，
逗號之後還是提示。`INDEX` 提交時補左括號，理由與內建函式相同。

日期部分只收完整名稱，不收 `yy`、`dd` 這些縮寫：縮寫背得起來的人不需要補字，
而 15 個名稱再乘上兩三種縮寫，清單就從「一眼看完」變成要捲動。

已知會誤判的是別種 `WITH (…)`：`CREATE INDEX … WITH (FILLFACTOR = 80)` 與
`OPENJSON(…) WITH (col int '$.x')` 也會列出資料表提示。沒有為它們再加判斷，
是因為那兩個位置本來也沒有正確答案——前者要的是索引選項，後者要的是使用者自己取的
資料行名稱，換掉的只是一份同樣不對的關鍵字清單。

`SET NOCOUNT ON` 這一類的工作階段選項**沒有**收進來。位置分不開：位置分析看到
`SET` 一律回報同一個位置，而 `UPDATE t SET |` 要的是資料行，跟 `SET NOCOUNT` 完全
相反。要分開得往回找 `UPDATE`／`MERGE`，那條路的成本高過它省下的幾個字。

## 關鍵字自動大寫

打完 `select` 再按空白鍵就得到 `SELECT`，不必先按 Tab 提交建議。
`inner`、`join`、`on`、`desc` 等關鍵字同樣適用；觸發時機是任何無法構成識別字的
字元——空白、逗號、括號、分號與運算子。

刻意不做成「用空白鍵提交清單選取項」：清單當下選中的可能是別的東西，
那種做法會把使用者根本沒要的名稱寫進編輯器。這裡只改寫剛打完的那一個字，
與清單開不開著無關，結果完全可預測。

下列情形不動：已經是大寫、限定字後方的名稱（`dbo.select`）、變數（`@select`）、
字串與註解內、方括號與雙引號識別字內（`[select]` 是欄位名稱）。

`GO` 也不動——它是 SSMS 的批次分隔符而不是 T-SQL 關鍵字，而且兩個字母的字太容易
誤傷別名（`FROM Loans go` 是合法的寫法）。它仍然會出現在建議清單裡。

由 `工具 → SqlAssist → 關鍵字轉大寫` 開關控制；關掉它不影響清單裡的關鍵字建議。

## 依上下文縮小建議範圍

| 游標前方 | 只顯示 | 提交行為 |
|---|---|---|
| `FROM`、`JOIN`、`UPDATE`、`INTO`、`USING` | Table、View、資料表值函式 | 插入名稱；函式補上引數 |
| `CROSS APPLY`、`OUTER APPLY` | 資料表值函式 | 補上引數 |
| `INSERT INTO` | Table、View | 展開欄位清單與 `VALUES` |
| `MERGE`／`MERGE INTO` | Table、View | 展開比對鍵、`UPDATE SET`、`INSERT` 與 `VALUES` |
| `ALTER PROCEDURE`／`PROC` | Procedure | 展開完整 ALTER 定義 |
| `ALTER FUNCTION` | 兩種函式 | 展開完整 ALTER 定義 |
| `ALTER VIEW` | View | 展開完整 ALTER 定義 |
| `ALTER TRIGGER` | Trigger | 展開完整 ALTER 定義 |
| `DROP PROCEDURE`／`PROC`、`DROP FUNCTION`、`DROP VIEW` | 同上各一類 | 插入名稱 |
| 其餘位置選到自訂函式（`SELECT `、`WHERE `…） | — | 補上引數 |
| `DROP`、`DISABLE`、`ENABLE TRIGGER` | Trigger | 插入名稱 |
| `ALTER`／`DROP`／`TRUNCATE TABLE` | Table、View | 插入名稱 |
| `NEXT VALUE FOR`、`ALTER`／`DROP SEQUENCE` | Sequence | 插入名稱 |
| `EXEC`、`EXECUTE` | Procedure | 展開具名參數清單 |
| `CREATE`／`ALTER`／`DROP INDEX`／`STATISTICS`／`TRIGGER` 之後的 `ON` | Table、View | 插入名稱 |
| `USE` | 這台伺服器上的資料庫 | 插入名稱 |
| `dbo.`、`[dbo].` | 該結構描述的物件 | 插入名稱 |
| `LibArchive.dbo.`、`LibArchive..` | 那個資料庫的物件 | 插入名稱 |
| `[192.0.2.10].[LibArchive].[dbo].` | — | 認得出來，但不給建議 |

`USING` 與 `FROM` 收在同一列不是為了湊數：MERGE 的來源與 FROM 的來源是同一條文法，
`SqlKeywordPositionAnalyzer` 與 `SqlScopeAnalyzer` 也早就這樣歸類。只有這一份漏掉時，
症狀是 `USING ` 之後完全沒有清單，而使用者看不出它和 `FROM ` 之後有什麼不同。

`IF EXISTS` 在比對前先剝掉一次，`DROP TABLE IF EXISTS `、`DROP TRIGGER IF EXISTS `
因此不必各寫一條加長版。剝除只砍尾端，前面每個詞元的位置都沒有位移，所以語句
關鍵字的起點仍然指得回原文；`IF EXISTS (SELECT …)` 那種流程控制剝完是空字串或
另一個語句的尾巴，兩者都推不出目標，與剝之前一樣不會有清單。
