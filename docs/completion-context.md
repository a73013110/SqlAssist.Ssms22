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

### `ON` 後面是資料表還是述詞

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

### MERGE 的動作子句

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

### 資料表值函式與純量函式分開

`SuggestionKind.TableFunction` 與 `SuggestionKind.Function` 是兩類：前者是內嵌
（`IF`）與多語句（`TF`）資料表值函式，後者是純量函式。中繼資料層的
`SqlObjectKinds.IsDataSource` 早就這樣分了，只有建議項這一層曾經把三種函式壓成
同一類——症狀是 `FROM dbo.fn_` 之後整份清單一個函式都沒有，
而使用者看不出它和資料表有什麼不同。

分完之後三個位置各自對了：

- `FROM`、`JOIN`、`USING` 之後多列資料表值函式，純量函式仍然不列——
  它回傳的是一個值，放在那裡剖析不過。
- `APPLY` 之後**只**列資料表值函式。那個位置文法上要的是資料表值函式或衍生資料表，
  `CROSS APPLY dbo.Loan` 剖析得過卻沒有意義；而純量函式從前是連帶列出來的雜訊。
  認的是 `APPLY` 一個字，前面的 `CROSS` 與 `OUTER` 不改變後面要什麼。
- `ALTER FUNCTION`、`DROP FUNCTION` 之後兩種都列：兩種都改得動也刪得掉。

反過來也不能把資料表值函式併進 `Table`：那樣 `ALTER FUNCTION` 之後就列不出它們了。

`APPLY` 有自己的 `CompletionTarget` 而不是共用 `Function`，還有第二個好處：
`CompletionTarget.Function` 因此收斂成「`ALTER`／`DROP FUNCTION` 的那個名稱」，
提交時分得出「這裡要補引數」還是「這裡只要名稱」——見下面的函式引數。

### 系統物件只在兩個位置拉進來

`sys.objects`、`sys.dm_exec_requests`、`sp_executesql`、`sp_help` 這些原本一個都列不出來
——第一層查詢寫死 `is_ms_shipped = 0`，結構描述清單也明確排除了 `sys` 與
`INFORMATION_SCHEMA`。

它們現在有自己的查詢，而且**與第一層分開、只在被問到時才跑**：光是一個使用者資料庫
底下就有一兩千列，併進第一層等於每一次開啟查詢視窗都多付兩倍代價，換來的東西九成的
時間沒有人要。只有兩個位置會問：

- 使用者自己打出了 `sys.` 或 `INFORMATION_SCHEMA.`
- 游標在 `EXEC ` 之後——`sp_executesql`、`sp_help` 一律不加結構描述就呼叫

`ALTER PROCEDURE ` 不算，雖然它的目標同樣是預存程序：系統程序改不動，列出來只會讓
使用者選到一個改不了的東西，與內建函式不進 `ALTER FUNCTION` 是同一條理由。

這一份也刻意**不設有效期**：系統物件跟著 SQL Server 的版本走，不會在一次工作階段
中途變動，查一次就用到換連線為止。

`sys` 與 `INFORMATION_SCHEMA` 這兩個結構描述名稱則不必等中繼資料——它們在每一個
資料庫裡都存在，是產品事實而不是誰的 schema。少了這兩筆的話，使用者連「打 `sys`
再按 Tab」這條路都沒有。

`DROP` 家族與它們對稱：`DROP PROCEDURE`、`DROP FUNCTION`、`DROP VIEW` 之後同樣
只列那一類，但意圖是 `Reference`——那個位置要的只是一個名稱，把整份定義放進去
反而讓語句不合法。少寫哪一條都沒有徵兆，只是使用者在那個位置沒有清單。

觸發程序、序列與使用者自訂的資料表型別**只在自己的位置出現**，不進一般清單。
理由與全域變數同一條：`SELECT tr` 不該冒出觸發程序，而 `EXEC ` 之後選到一個觸發
程序一定執行失敗。觸發程序算模組（`OBJECT_DEFINITION` 拿得到定義），所以
`ALTER TRIGGER` 與 `ALTER PROCEDURE` 一樣直接展開完整定義。

這三種都不在 `sys.objects` 的原白名單裡，第一層查詢因此多收 `TR`、`TA`、`SO`，
並把 `sys.table_types` 另外 UNION 進來貼上 `TT` 標籤——與同義字的 `SN` 同一個做法。
資料表型別取的是 `type_table_object_id` 而不是 `user_type_id`：快取以 object_id 為鍵，
用型別自己的識別碼會與真的物件撞在一起，而那個 object_id 同時正好是它的欄位在
`sys.columns` 裡的鍵，於是欄位與滑鼠停留提示都不必另外接。

這一份不對資料庫送出任何查詢，因此與「列出資料庫物件」的設定無關；
也只在游標真的落在資料來源位置、而且沒有限定字時才掃——`FROM dbo.` 之後不該
出現沒有結構描述的名稱，而這條路徑在每一次按鍵上。

`ap` → `Tab` → 選取程序 → `Tab`，編輯器會直接放進該程序可執行的完整定義，
可以立刻修改並更新。定義開頭的 `CREATE` 或 `CREATE OR ALTER` 會改寫成 `ALTER`，
主體完全不動（主體裡的 `CREATE TABLE #tmp` 之類的語句不受影響），游標停在標頭的
物件名稱之後。`ALTER FUNCTION`、`ALTER VIEW`、`ALTER TRIGGER` 走的是同一份展開，
行為完全一致——那四種在 `SqlObjectKinds.IsModule` 裡是同一類，`OBJECT_DEFINITION`
都拿得到定義。

定義取不到只有兩個原因（物件是 `WITH ENCRYPTION` 建的，或這個登入沒有它的
`VIEW DEFINITION` 權限），這時維持只插入名稱，並在診斷紀錄裡寫明。

## 限定字是一條路徑，不是一個識別字

點號前方那幾段一起讀進 `SqlObjectPath`（`Core/Parsing`），最右邊一段是結構描述或
別名，往左依序是資料庫與連結伺服器。只讀最右邊一段有兩個症狀，而兩個都沒有徵兆：
清單改列**目前連線**的 `dbo` 物件，而剝完之後的位置判斷停在 `FROM LibArchive.` 上，
連 `FROM` 都看不到，建議目標退成 `Any`——關鍵字與片段於是混了進來。

三條規則跟著路徑走，各寫一份的話同一個名稱會在某條路徑上認得、在另一條上不認得：

- **右對齊。** 省略一律從左邊省，所以最右邊永遠是名稱（限定字則是結構描述）。
- **空的中間段當成沒寫。** `LibArchive..` 少的是結構描述，不是資料庫。存成空字串的話
  下游會拿它去比對，而沒有任何結構描述叫做空字串。
- **超過上限就整個不認。** 取最右邊那幾段的話，使用者打錯的一串名稱會安靜地
  變成一個查得到的東西。

多段的限定字**不會**被當成別名：別名只有一段。拿最右邊那一段去比對別名的話，
剛好取名叫 `dbo` 的別名會把清單換成它的欄位。

「有沒有限定字」一律問 `QualifierPath` 而不是問 `Qualifier`：`LibArchive..` 有路徑
卻沒有結構描述那一段，問錯的症狀是插入文字自己補上 `[dbo].`，寫出
`LibArchive..[dbo].[Loan]`。
