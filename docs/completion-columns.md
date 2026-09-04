# 欄位建議

別名指向哪些來源、指令碼自己宣告的資料表怎麼讀、範圍在哪裡切開，以及詞元結束後
清單要怎麼重開。欄位提交後展開成整句的規則見
[展開內容](statement-values.md)，清單怎麼排名與
觸發見 [completion.md](completion.md)。

輸入 `別名.` 或 `資料表名稱.` 時列出該資料來源的欄位，並顯示型別、NULL 與 PK。

別名解析需要看得到游標**後方**的文字：

```sql
SELECT u.| FROM dbo.Lib_Reader u
```

FROM 子句在游標之後，只看前文永遠解析不出 `u`——而編輯既有查詢正是最常
遇到這種情形的時候。因此上下文分析改用完整文字加游標位置的多載。

## 別名指向哪些欄位

「這個別名給得出哪些欄位」與展開 `SELECT *` 是同一個問題，答案只有一份：
`Core/Parsing/SqlColumnSourceResolver`。資料表與檢視交給中繼資料層，
子查詢與 CTE 的輸出欄位直接讀它們的選取清單，內層自己又是 `*` 時遞迴下去。
細節見 [wildcard-expansion.md](wildcard-expansion.md#欄位從哪裡來)。

各寫一份的症狀曾經就在眼前：同一段 SQL 的 `a.*` 按 Tab 展得開，`a.` 卻一個建議
都沒有——只有萬用字元那一份會往子查詢裡看，欄位建議那一份遇到衍生資料表就放棄，
遇到 CTE 名稱則當成資料庫裡的資料表去查，而那張資料表並不存在。

```sql
SELECT a.| FROM (SELECT c.PUBL_CODE, c.SHELF_LOCATION_CODE FROM dbo.PUBLISHER c) a
;WITH c AS (SELECT Id FROM dbo.Item) SELECT x.| FROM c x
```

暫存資料表與資料表變數走的是同一條路：它們的欄位中繼資料一列都查不到——
資料表變數不是 `sys.objects` 裡的物件，暫存資料表在 tempdb 裡，而擴充只查目前連線的
那一個資料庫——但那些欄位就寫在使用者眼前的 `CREATE TABLE #Loan (…)` 與
`DECLARE @rows TABLE (…)` 括號裡，讀得出來，見[指令碼宣告的資料表](#指令碼宣告的資料表)。

只有讀不出宣告的時候才放棄（例如 `SELECT … INTO #Loan` 建立的暫存資料表，
那裡沒有資料行定義）。放棄時維持原本的結構描述解讀，讓使用者至少還看得到物件清單。

子查詢與 CTE 讀出來的欄位沒有型別、NULL 與 PK——那些要追到最內層的資料表，
而中間任何一段運算式都會讓答案不成立。說明欄因此只寫「查詢結果」。

## 資料表值函式的別名

```sql
SELECT f.| FROM dbo.fn_LoansByReader(0) f
SELECT * FROM dbo.Loan l CROSS APPLY dbo.fn_LoansByReader(l.CopyNo) f
```

`f` 與資料表的別名走同一條路：範圍分析把引數清單整段跳過——引數裡的逗號不是來源
清單的逗號，巢狀的括號也要一次跳完——攤平出來的仍然是一個中繼資料來源，
欄位由第二層的 `sys.columns` 給，見 [metadata.md](metadata.md) 的
「資料行查得到，不代表它是一張資料表」。`SELECT *` 展開同理。

只有提交後的展開閘門要另外問一句：`INSERT INTO dbo.fn_LoansByReader` 剖析不過，
所以那裡問的是「插得進去嗎」而不是「查不查得到資料行」——不然選一個函式就會把一段
跑不動的骨架整句蓋在使用者打的那一行上。

## 指令碼宣告的資料表

`#Loan` 與 `@rows` 的欄位中繼資料一列都查不到，但它們的宣告就在使用者眼前：

```sql
CREATE TABLE #Loan (Id INT IDENTITY(1,1) PRIMARY KEY, CopyNo NVARCHAR(20) NOT NULL);
DECLARE @rows TABLE (Id INT IDENTITY(1,1) PRIMARY KEY, CopyNo NVARCHAR(20) NOT NULL);
```

那份括號由 `Core/Parsing/SqlScriptTableCollector` 讀出來，接進
`SqlColumnSourceResolver`——於是**同一次修改讓四個位置一起活過來**：`SET |` 與
`WHERE |` 的欄位建議、`#Loan.` 與 `@rows.` 的欄位、`SELECT *` 按 Tab 的展開，
以及提交 `INSERT INTO`／`MERGE INTO` 之後的整句展開。各自接一條的話，
漏掉的那一條沒有徵兆，只是使用者在那個位置又得把每個欄位重打一遍。

只認**帶著資料行定義**的兩種寫法。`SELECT … INTO #Loan` 不在裡面：那裡沒有型別，
而少了型別的 `INSERT` 骨架會替使用者猜錯字面值——那張表的**名稱**仍然照列，
名稱與欄位是兩件事。`RETURNS @rows TABLE (…)` 則免費一起認得，
因為認的是「變數 `TABLE (`」這個形狀本身。

`CREATE TABLE` 這兩個字是必要條件而不是修飾：`INSERT INTO #Loan (CopyNo, ReaderId)`
的形狀與資料行清單一模一樣，少了前綴就會把使用者剛寫的 `INSERT` 讀成一份宣告，
而那份假宣告裡每個欄位都沒有型別，還會蓋掉真正的那一份。

一般資料表（`CREATE TABLE dbo.Loan (…)`）也不收：它在中繼資料裡，
而那一份回答的是「現在長什麼樣」，指令碼裡這一份回答的是「正要變成什麼樣」。

讀出來的資料行在 `Metadata/Model/SqlScriptTableDetail` 換成中繼資料層的欄位模型，
目的只有一個——**不要有第二份「哪些欄位插得進去」**。換過來之後，暫存資料表與
資料表變數走的就是資料庫物件那一份展開，[排除規則](statement-values.md#哪些欄位插不進去)與
[值先填什麼](statement-values.md#值先填什麼)一個字都不必重寫。
