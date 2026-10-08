# 上下文收斂

游標前方的文字決定清單裡剩下什麼：哪些位置只剩固定那幾個字、哪些位置要把整類項目
拿掉；關鍵字自動大寫見[自動大寫](keyword-casing.md)。判斷的出處是
`Core/Completion/SqlCompletionContextAnalyzer`，它讀的是實際文字而不是任何宣告。
可不可補、開不開、預設選不選由 `SqlCompletionSlot` 分類，
`SqlCompletionPolicy` 一條規則決定，見[名稱位置](completion-name-slots.md#三分類)。
排名見 [completion.md](completion.md)。

## 引數與提示的封閉清單

這幾個位置除了那一份清單以外沒有別的東西是對的，與資料型別是同一種判斷、
同一個代價權衡——判定成立時整份清單就換掉，所以只收看得出來的：

```text
SELECT DATEADD(|              → DAY、MONTH、YEAR…（日期部分）
SELECT * FROM dbo.Loan WITH (| → NOLOCK、UPDLOCK、INDEX(…（資料表提示）
SELECT * FROM dbo.Loan OPTION (| → RECOMPILE、MAXDOP、FORCE ORDER…（查詢提示）
SELECT {fn |                  → CURRENT_TIME、USER、RIGHT…（ODBC 純量函式）
```

都認得出來，是因為游標**前面**那個字就把話說完了。CTE 的 `WITH` 不會誤判——
`;WITH c AS (` 的 `WITH` 與左括號之間隔著一個名稱。

日期部分只在**第一個**引數：打過逗號之後那裡要的是數字與日期。哪些函式由函式目錄的簽章說
（`DATE_BUCKET(datepart, …)`），不另列名單；只收完整名稱，不收 `yy`、`dd`：收了清單就要捲動。
提示是一份清單，逗號之後還是提示，`OPTION (TABLE HINT (t, ` 之後也是資料表提示；`INDEX` 提交時補左括號，理由與內建函式相同。
ODBC 收附錄 E 整份：sql-docs 的表格只列與 T-SQL 不重複的，引擎認整份。
`{fn EXTRACT(`、`TIMESTAMPADD(` 第一個引數問 ODBC 簽章：`extract_field` 列附錄 E 的六個，`interval` 列九個 `SQL_TSI_`。

`CREATE INDEX … WITH (` 列的是索引選項（片語，見[子句片語](completion-phrases.md)）。
已知會誤判的是 `OPENJSON(…) WITH (col int '$.x')`：也會列出資料表提示。沒有為它再加判斷：
那裡要的是使用者自己取的資料行名稱，換掉的只是一份同樣不對的清單。

`SET NOCOUNT ` 之後的 `ON`、`OFF` 不是封閉清單，走關鍵字位置 `SetOptionValue`（名稱不列）：
識別字的值（`SET DATEFORMAT dmy`）寫完換行還接得了下一句，整份換掉就錯了。規則見
[子句邊界](completion-boundaries.md)。

## 依上下文縮小建議範圍

| 游標前方 | 只顯示 | 提交行為 |
|---|---|---|
| `FROM`、`JOIN`、`UPDATE`、`INTO`、`USING` | Table、View、資料表值函式 | 插入名稱；函式補上括號 |
| `CROSS APPLY`、`OUTER APPLY` | 資料表值函式 | 補上括號 |
| `INSERT INTO` | Table、View | 展開欄位清單與 `VALUES` |
| `MERGE`／`MERGE INTO` | Table、View | 展開比對鍵、`UPDATE SET`、`INSERT` 與 `VALUES` |
| `ALTER`（含 `CREATE OR ALTER`）之後的種類，見[第一層物件](completion-catalog-names.md#第一層物件) | 那一類；`TABLE` 是 Table、View，函式兩種 | 模組展開完整 ALTER 定義 |
| `DROP`、`TRUNCATE`、`ENABLE`、`DISABLE` 之後的種類，`DROP` 名稱清單的逗號之後（`DROP TABLE #a, `） | 同上；`SYNONYM` 是同義字，`TYPE` 是資料表型別 | 插入名稱 |
| 其餘位置選到自訂函式（`SELECT `、`WHERE `…） | — | 補上括號 |
| `WITH RESULT SETS (AS OBJECT` | Table、View | 插入名稱 |
| `WITH RESULT SETS (AS TYPE` | 使用者自訂資料表型別 | 插入名稱 |
| `NEXT VALUE FOR` | Sequence | 插入名稱 |
| `EXEC`、`EXECUTE` | Procedure | 展開具名參數清單 |
| `CREATE`／`ALTER`／`DROP INDEX`／`STATISTICS`／`TRIGGER` 之後的 `ON` | Table、View | 插入名稱 |
| 開始一句的 `USE`（函式引數裡的 `USE MODEL` 不是） | 這台伺服器上的資料庫 | 插入名稱 |
| `OPEN`、`CLOSE`、`DEALLOCATE`、`FETCH [… FROM]`、`WHERE CURRENT OF`，可夾 `GLOBAL` | 指令碼 `DECLARE c CURSOR` 宣告的資料指標與資料指標變數，另列片語的字（`FETCH ` 的方向、`OPEN `／`CLOSE ` 的金鑰） | 插入名稱 |
| `OVER`、`OVER (`、`WINDOW w AS (` | 這個查詢 WINDOW 子句取的具名視窗；括號之後另列 `PARTITION`、`ORDER` | 插入名稱 |
| `COLLATE`、`SET LANGUAGE`、`DEFAULT_LANGUAGE =`、`AT TIME ZONE` | [執行個體名單](completion-instance-lists.md)與指令碼已用值 | 依名單的寫法 |
| `FROM a, `、`FROM a, LibArchive.` | 同 `FROM` 那一列 | 插入名稱 |
| `UPDATE t SET `、`INSERT INTO t (`、`ON t (` 這種資料行的位置 | 那張表的資料行，見[欄位](completion-columns.md#文法指定的所屬資料表) | 插入名稱 |
| `dbo.`、`[dbo].` | 該結構描述的物件 | 插入名稱 |
| `LibArchive.dbo.`、`LibArchive..` | 那個資料庫的物件 | 插入名稱 |
| `[192.0.2.10].[LibArchive].[dbo].` | — | 認得出來，但不給建議 |

表中「只顯示」之外，寫得出名稱開頭的列（`USE`、執行個體名單與限定字那幾列除外）
另列結構描述、資料庫與連結伺服器，見[限定名稱](qualified-names.md#右對齊猜錯時整條往左挪)。

`USING` 與 `FROM` 收在同一列：MERGE 的來源與 FROM 的來源是同一條文法，
`SqlKeywordPositionAnalyzer` 與 `SqlScopeAnalyzer` 也這樣歸類。
漏掉時 `USING ` 之後沒有清單。
`FROM`、`INTO` 之後接不接資料來源見[語句的界線](completion-boundaries.md#語句的界線)。資料指標那一格由位置分析的
`IntroducesCursor` 一條認，位置與目標共用；名稱前的 `GLOBAL` 是修飾字，當成名稱就只剩 `INTO`。
名稱只在指令碼裡，不查資料庫；宣告的認法（`Parsing/SqlCursorDeclaration`）與 `CursorOption` 共用。
資料指標變數也收（`DECLARE @c [AS] CURSOR`、`SET @c = CURSOR`），`DEALLOCATE @` 照樣是這一格；指派給它的 `SET @c = `
另列這份名冊，目標仍是一般位置，`CURSOR` 才不被濾掉；
選項只接在給內容的地方（具名宣告、`SET @c = CURSOR`），變數的宣告後面只有逗號或下一句。

逗號那一列不靠前導關鍵字：前一、兩個詞元只有一個逗號，答案來自
`SqlKeywordPositionAnalyzer` 的位置（逗號回到清單起點），這裡不再自己回頭找 `FROM`。
只多問一次括號，把 `INSERT INTO T (a, ` 的資料行清單排除；括號裡裝的是查詢時仍然
算數，那是衍生資料表自己的 `FROM` 清單。少了這一列有兩個症狀：逗號之後空前綴時
整份上下文不參與，以及 `FROM a, LibArchive.` 的限定字被當成別名，列出一張不存在的資料表的欄位。

`IF EXISTS` 在比對前先剝掉一次，`DROP TABLE IF EXISTS `、`DROP TRIGGER IF EXISTS `
因此不必各寫一條加長版。`IF EXISTS (SELECT …)` 那種流程控制剝完是空字串或
另一個語句的尾巴，兩者都推不出目標，與剝之前一樣不會有清單。

## 左方括號之後

T-SQL 的 `[` 只有一個意思：名稱從這裡開始。所以它和限定字一樣把範圍講完了——
位置、限定字與目標照沒打方括號時判斷，空前綴也開清單，但只列包得起來的種類
（物件、結構描述、資料庫、連結伺服器、欄位、指令碼的資料來源與別名，純量位置的資料表變數 `[@rows]`）；關鍵字、
內建函式、型別與提示包起來會變成另一個東西。三處問的都是
`SqlCompletionContext.Bracketed`。

詞元起點是左方括號，篩選時拿掉它、還原 `]]`。使用者自己打了 `]` 就是打完了：
篩選字帶著它比對落空，清單跟著關。跨行或超過 128 字元的左方括號不算，否則一個
忘了關的 `[` 會讓底下整份指令碼都變成名稱；判斷在 `SqlIdentifier.FindOpenBracket`。
提交時怎麼包見[插入文字](completion-insertion.md#方括號加在哪些名稱上)。
