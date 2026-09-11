# 欄位建議

別名指向哪些來源、指令碼自己宣告的資料表怎麼讀、範圍在哪裡切開，以及詞元結束後
清單要怎麼重開。欄位提交後展開成整句見[展開內容](statement-values.md)，排名與觸發
見 [completion.md](completion.md)。

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

別名後面寫出來的資料行清單（`AS T (ID, Name)`）覆寫主體算出來的名稱，與 CTE 的
`WITH c (a, b)` 同一份實作；`(VALUES …)` 不是 `SELECT`，只有這條路。文法只讓
**衍生資料表與 `OPENROWSET` 那族**收得下：`dbo.fn(x) f (NOLOCK)` 形狀一樣卻是提示，
而 `NOLOCK` 不是保留字，猜括號內容會讓 `SELECT * INTO #Temp` 的結構變成一個假欄位。

暫存資料表與資料表變數走的是同一條路：欄位的中繼資料一列都查不到——資料表變數
不是 `sys.objects` 裡的物件，暫存資料表在 tempdb 裡——但那些欄位就寫在使用者眼前的
`CREATE TABLE #Loan (…)` 與 `DECLARE @rows TABLE (…)` 括號裡，
見[指令碼宣告的資料表](#指令碼宣告的資料表)。

`SELECT … INTO #Loan` 沒有那份括號，欄位卻同樣寫在眼前，
見[投影出來的暫存資料表](#投影出來的暫存資料表)。真的讀不出來時才放棄，並維持原本的
結構描述解讀，讓使用者至少看得到物件清單。

子查詢與 CTE 讀出來的欄位沒有型別、NULL 與 PK——那些要追到最內層的資料表，
而中間任何一段運算式都會讓答案不成立。說明欄因此只寫「查詢結果」。

系統檢視（`FROM sys.triggers`）的欄位走同一條路，兩處不同都收斂在中繼資料層，
見[物件種類](completion-object-kinds.md)。

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

## 指令碼宣告的資料表

`#Loan` 與 `@rows` 的欄位中繼資料一列都查不到，但它們的宣告就在使用者眼前：

```sql
CREATE TABLE #Loan (Id INT IDENTITY(1,1) PRIMARY KEY, CopyNo NVARCHAR(20) NOT NULL);
DECLARE @rows TABLE (Id INT, CopyNo NVARCHAR(20));
```

那份括號由 `Core/Parsing/SqlScriptTableCollector` 讀出來，接進
`SqlColumnSourceResolver`——於是**同一次修改讓四個位置一起活過來**：`SET |` 與
`WHERE |` 的欄位建議、`#Loan.` 與 `@rows.` 的欄位、`SELECT *` 按 Tab 的展開，
以及提交 `INSERT INTO`／`MERGE INTO` 之後的整句展開。各自接一條的話，漏掉的那一條
沒有徵兆——使用者只是在那裡又得把每個欄位重打一遍。

只認**帶著資料行定義**的兩種寫法。`SELECT … INTO #Loan` 不在這一份名冊裡：
那裡沒有型別，而少了型別的 `INSERT` 骨架會替使用者猜錯字面值。
`RETURNS @rows TABLE (…)` 則免費一起認得，因為認的是「變數 `TABLE (`」這個形狀。

`CREATE TABLE` 這兩個字是必要條件而不是修飾：`INSERT INTO #Loan (CopyNo, ReaderId)`
的形狀與資料行清單一模一樣，少了前綴就會把使用者剛寫的 `INSERT` 讀成一份宣告，
而那份假宣告裡每個欄位都沒有型別，還會蓋掉真正的那一份。

一般資料表（`CREATE TABLE dbo.Loan (…)`）也不收：它在中繼資料裡，
而那一份回答「現在長什麼樣」，指令碼裡這一份回答「正要變成什麼樣」。

### 投影出來的暫存資料表

`SELECT … INTO #Loan` 讀不出的是**型別**，不是名稱——欄位就寫在那句 `SELECT` 的
選取清單裡。它與 CTE 同一個形狀，走同一份遞迴，`SELECT *` 往它讀的那張表攤平
下去。與帶型別的宣告合成同一個 `SqlScriptTable`
（唯一出處 `FindScriptTable`），下游一個字都不必分辨；沒有型別的欄位填 `NULL`，
見[展開內容](statement-values.md#值先填什麼)。

從 `SELECT` 往前認而不是從 `INTO` 往回認（`INSERT INTO #Loan (…)` 形狀一模一樣）；
同理 `ExtractSources` 在 `SELECT` 開頭的敘述裡不收 `INTO` 的目標——那張表是**正要
建立**的，收了會讓 `WHERE |` 把它跟真正的來源混著列。

資料行**延後**算：`FROM ` 之後只要名稱，投影卻要整段遞迴攤平；算完記在那張表上。
投影不出來（`SELECT * INTO #Loan`）時資料行是空的：預覽說實情，提交退回只補名稱
——貼一個空括號的 `INSERT` 比什麼都不做糟。

### 名稱也要出現在 `FROM` 之後

同一份宣告回答的不只是欄位：使用者寫完 `DECLARE @rows TABLE (…)`，下一行打
`SELECT * FROM ` 要的就是那個名稱，而中繼資料查不到它。CTE、暫存資料表與
資料表變數因此併在同一份 `Core/Completion/SqlScriptDataSourceSuggestions` 裡。

只有暫存資料表看形狀就分得完：井號開頭的識別字在 T-SQL 裡只有這一種意思。
資料表變數不行——`@rows` 與 `@readerId` 是同一種詞元，分辨的憑據只有那份宣告本身，
所以這一種只認名冊裡讀得出資料行的。少了這一條的症狀是 `FROM ` 之後列出每一個
純量變數，而它們一個都插不進那個位置；整份不收則要使用者先打一個小老鼠，
換到[變數](completion-variables.md)那份清單去找。

三種在清單裡共用同一個圖示（`Ssms22/UI/SqlIcons`），與資料庫資料表分得開：它們
回答的是同一件事——一張只活在這份文字裡的表，連線一斷就沒了。資料表變數曾經
跟著區域變數走，症狀是 `#Loan` 與 `@rows` 長得像兩種東西。
讀不出資料行的 `@readerId` 不在此列，它在清單與預覽裡仍然是一個變數。

讀出來的資料行在 `Metadata/Model/SqlScriptTableDetail` 換成中繼資料層的欄位模型，
目的只有一個——**不要有第二份「哪些欄位插得進去」**。換過來之後，暫存資料表與
資料表變數走的就是資料庫物件那一份展開，
[排除規則](statement-values.md#哪些欄位插不進去)與[值先填什麼](statement-values.md#值先填什麼)不必重寫。
