# 欄位建議

別名指向哪些來源、文法指定的所屬資料表，以及範圍在哪裡切開。指令碼自己宣告的
資料表（`#Temp`、`@table`）見[指令碼宣告的資料表](script-tables.md)。
欄位提交後展開成整句見[展開內容](statement-values.md)，排名與觸發見
[completion.md](completion.md)。

輸入 `別名.` 或 `資料表名稱.` 時列出該資料來源的欄位，並顯示型別、NULL 與 PK。

別名解析需要看得到游標**後方**的文字：`SELECT u.| FROM dbo.Lib_Reader u` 的 FROM
子句在游標之後，只看前文永遠解析不出 `u`——而編輯既有查詢正是最常遇到這種情形的
時候。因此上下文分析改用完整文字加游標位置的多載。

## 別名指向哪些欄位

「這個別名給得出哪些欄位」與展開 `SELECT *` 是同一個問題，答案只有一份：
`Core/Parsing/SqlColumnSourceResolver`。資料表與檢視交給中繼資料層，
子查詢與 CTE 直接讀它們的選取清單，內層自己又是 `*` 時遞迴下去。
細節見 [wildcard-expansion.md](wildcard-expansion.md#欄位從哪裡來)。

各寫一份的症狀曾經就在眼前：同一段 SQL 的 `a.*` 按 Tab 展得開，`a.` 卻一個建議
都沒有——只有萬用字元那一份會往子查詢裡看，另一份遇到衍生資料表就放棄，
遇到 CTE 名稱則去查一張不存在的表。

```sql
SELECT a.| FROM (SELECT c.PUBL_CODE FROM dbo.PUBLISHER c) a
;WITH c AS (SELECT Id FROM dbo.Copy) SELECT x.| FROM c x
```

選取項的名字可以是字串（`AS 'Seq'`、`'Seq' = …`）：一項叫不出名字，整個衍生資料表就攤不開，
別名一個欄位都沒有。

別名後面寫出來的資料行清單（`AS T (ID, Name)`）覆寫主體算出來的名稱，與 CTE 的
`WITH c (a, b)` 同一份實作；`(VALUES …)` 不是 `SELECT`，只有這條路。是不是清單先看來源：
衍生資料表與 `OPENROWSET` 那族只接清單，具名資料表只接舊式提示。使用者定義的函式兩種都寫得出來
（`dbo.fn(x) f (NOLOCK)`、`dbo.fn(x) AS s (CopyNo)`），只有它看內容：每一項都以資料表提示開頭才是提示。
整份當清單會讓 `SELECT * INTO #Temp` 的結構變成一個叫 NOLOCK 的假欄位。

暫存資料表與資料表變數走的是同一條路：欄位的中繼資料一列都查不到——資料表變數
不是 `sys.objects` 裡的物件，暫存資料表在 tempdb 裡——但那些欄位就寫在使用者眼前的
`CREATE TABLE #Loan (…)` 與 `DECLARE @rows TABLE (…)` 括號裡，
見[指令碼宣告的資料表](script-tables.md)。

`SELECT … INTO #Loan` 沒有那份括號，欄位卻同樣寫在眼前，
見[投影出來的暫存資料表](script-tables.md#投影出來的暫存資料表)。真的讀不出來時才放棄，並維持原本的
結構描述解讀，讓使用者至少看得到物件清單。

子查詢與 CTE 讀出來的欄位沒有型別、NULL 與 PK——那些要追到最內層的資料表，
而中間任何一段運算式都會讓答案不成立。說明欄因此只寫「查詢結果」。

系統檢視（`FROM sys.triggers`）的欄位走同一條路，兩處不同都收斂在中繼資料層，
見[物件種類](completion-object-kinds.md)。

## 文法指定的所屬資料表

`UPDATE t SET |` 與 `t.|` 要的是同一份資料行：那是省略掉的限定字。只看前文時
`SqlColumnOwner` 記下寫在那裡的名稱（`SqlCompletionContext.ColumnOwner`），全文分析與限定字
同一條解法——單段名稱先當別名問範圍（`UPDATE l SET … FROM dbo.Loan l`）——換成
`Column` 目標。候選集合因此封閉，空前綴就開；清單只有資料行與片語的字（`DROP COLUMN IF EXISTS`），
工作階段選項（`ROWCOUNT`）不列。

| 位置 | 所屬資料表 |
|---|---|
| `UPDATE [TOP (n)] t [WITH (…)] SET `、指派的逗號之後 | `t` |
| MERGE 的 `THEN UPDATE SET `、`THEN INSERT (` | MERGE 的目標 |
| `INSERT [INTO] t (`、逗號之後 | `t` |
| `CREATE INDEX`／`STATISTICS … ON t (`、`INCLUDE (`、`REFERENCES u (` | `t`、`u` |
| `ALTER TABLE t ALTER COLUMN `／`DROP COLUMN ` | `t` |

不算的位置：`ORDER BY `、`GROUP BY ` 接得了運算式與序號；`EXEC p ` 的位置引數可以直接寫常值，
`WITH` 也接在那裡（打 `@` 才列參數）；`CREATE TABLE` 的條件約束清單屬於一張還不存在的表。
指派左邊寫限定字的（`SET t.Fee`）打 `t.` 就重開成同一份，別名本身不列。

`UPDATE t⏎SET` 的 SET 仍是指派：片語前一格的換行只給真的開始一句的字，
見[子句片語](phrase-generator.md#前一格)。

## 資料表值函式的別名

`SELECT f.| FROM dbo.Loan l CROSS APPLY dbo.fn_LoansByReader(l.CopyNo) f` 的 `f`
與資料表的別名走同一條路：範圍分析把來源自己帶的括號整段跳完——引數清單、資料表
提示與 `TABLESAMPLE` 都算，裡面的逗號不是來源清單的逗號——攤平出來的仍然是一個
中繼資料來源，
欄位由第二層的 `sys.columns` 給，見 [metadata.md](metadata.md) 的
「資料行查得到，不代表它是一張資料表」。

只有提交後的展開閘門要另外問一句：`INSERT INTO dbo.fn_LoansByReader` 剖析不過，
那裡問的是「插得進去嗎」而不是「查不查得到資料行」——不然選一個函式就會把一段跑不
動的骨架蓋在使用者打的那一行上。

`#Loan`、`@rows` 等指令碼自己宣告的資料表怎麼解析欄位，
見[指令碼宣告的資料表](script-tables.md)。

## inserted 與 deleted

兩個限定字，欄位是它們指的那張表的（`Parsing/SqlChangeTables`）：DML 觸發程序的主體裡指父資料表，
OUTPUT 子句裡指那句 DML 的目標，目標寫成別名時看別名的來源；兩種都在時看最內層。DDL 與登入觸發程序沒有它們。

- 和別名同一份清單（`SqlStatementScope.ChangeTables`）。觸發程序裡要寫在 FROM 才算數，但選取清單
  寫在 FROM 之前，所以整個主體都列。不併進沒寫限定字的來源：OUTPUT 的欄位一定要寫限定字。
- 觸發程序裡 `FROM ` 之後列成資料來源；寫出來的 `FROM inserted i` 換成父資料表，`i.` 才有欄位。
