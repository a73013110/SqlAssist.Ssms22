# 指令碼宣告的資料表

`#Loan`、`@rows` 等指令碼裡才有的資料表，欄位建議怎麼讀。別名與一般資料表／TVF
的欄位解析見[欄位](completion-columns.md)。

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

變數後的點號只有資料表變數算限定字：`@rows.` 列它的資料行（提交時改寫成 `[@rows].`，
見[插入文字](completion-insertion.md#欄位的限定字另有一條)）；純量變數的 `@x.value(` 是 xml 方法，
歸 `Inert`，不開清單。

只認**帶著資料行定義**的兩種寫法。`SELECT … INTO #Loan` 不在這一份名冊裡：
那裡沒有型別，而少了型別的 `INSERT` 骨架會替使用者猜錯字面值。
`RETURNS @rows TABLE (…)` 則免費一起認得，因為認的是「變數 `[AS] TABLE (`」這個形狀。
`AS` 與 `DECLARE @n AS INT` 一樣可有可無，少認它的症狀是那張表的別名一個欄位都沒有。

`CREATE TABLE` 這兩個字是必要條件而不是修飾：`INSERT INTO #Loan (CopyNo, ReaderId)`
的形狀與資料行清單一模一樣，少了前綴就會把使用者剛寫的 `INSERT` 讀成一份宣告，
而那份假宣告裡每個欄位都沒有型別，還會蓋掉真正的那一份。

一般資料表（`CREATE TABLE dbo.Loan (…)`）也不收：它在中繼資料裡，
而那一份回答「現在長什麼樣」，指令碼裡這一份回答「正要變成什麼樣」。

## 投影出來的暫存資料表

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

## 名稱也要出現在 `FROM` 之後

同一份宣告回答的不只是欄位：使用者寫完 `DECLARE @rows TABLE (…)`，下一行打
`SELECT * FROM ` 要的就是那個名稱，而中繼資料查不到它。CTE、暫存資料表與
資料表變數因此併在同一份 `Core/Completion/SqlScriptObjectSuggestions` 裡。

暫存資料表不分辨是哪一句建立的（`CREATE TABLE`、`SELECT INTO`、`INSERT INTO` 各認一次，
漏掉的寫法就安靜地少一個名稱），反過來認**暫存程序**：井號名稱只有這兩種意思，而程序
名稱只寫在 `PROCEDURE`／`PROC` 或 `EXEC`／`EXECUTE`（含 `EXEC @rc =`）之後。那些名稱
改列在 `EXEC ` 之後，種類與資料庫的程序相同；沒有結構描述、沒有中繼資料，提交只補名稱。
全當成資料表的症狀是 `EXEC #` 沒有清單，`FROM ` 之後卻列出一個選了就失敗的程序。

資料表變數不行——`@rows` 與 `@readerId` 是同一種詞元，分辨的憑據只有那份宣告本身，
所以這一種只認名冊裡讀得出資料行的。少了這一條的症狀是 `FROM ` 之後列出每一個
純量變數，而它們一個都插不進那個位置；整份不收則要使用者先打一個小老鼠，
換到[變數](completion-variables.md)那份清單去找。
資料表值參數（`@rows dbo.LoanRows READONLY`）也在名冊裡：`READONLY` 只寫在資料表型別的參數上，憑它就分得出來。
資料行在型別定義裡，同一份指令碼寫了 `CREATE TYPE … AS TABLE (…)` 才讀得到，否則只有名稱；
沒有 `READONLY` 的 `DECLARE @t dbo.LoanRows` 只在同一份指令碼定義了那個資料表型別時收（`FROM`、`INSERT INTO` 都列），否則分不出是資料表還是別名型別。
宣告成 `xml` 的變數也收：資料來源的位置接得了 `@x.nodes('/a') T(c)`。提交只補名稱，點號之後沒有方法清單，
由使用者自己打。

三種在清單裡共用同一個圖示（`Ssms22/UI/SqlIcons`），與資料庫資料表分得開：它們
回答的是同一件事——一張只活在這份文字裡的表，連線一斷就沒了。資料表變數曾經
跟著區域變數走，症狀是 `#Loan` 與 `@rows` 長得像兩種東西。
讀不出資料行的 `@readerId` 不在此列，它在清單與預覽裡仍然是一個變數。

讀出來的資料行在 `Metadata/Model/SqlScriptTableDetail` 換成中繼資料層的欄位模型，
目的只有一個——**不要有第二份「哪些欄位插得進去」**。換過來之後，暫存資料表與
資料表變數走的就是資料庫物件那一份展開，
[排除規則](statement-values.md#哪些欄位插不進去)與[值先填什麼](statement-values.md#值先填什麼)不必重寫。
