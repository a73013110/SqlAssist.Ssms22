# 提交後展開成整句

本頁處理展開範圍、寫回安全、游標落點、MERGE 與復原。欄位、參數與預留值見 [展開內容](statement-values.md)。

## 類型與安全寫回

有四個位置提交的不是一個名稱，而是一整句：`ALTER PROCEDURE` 之後放進完整定義，
`INSERT INTO` 之後放進欄位清單與 `VALUES`，`MERGE INTO` 之後放進比對鍵與兩個動作
子句，`EXEC` 之後放進具名傳值的參數清單。第五種只換掉剛插入的那個名稱，
見[函式的引數](statement-values.md#提交函式時補上引數)。

`INSERT INTO #Loan` 與 `INSERT INTO @rows` 走的是完全同一條路，差別只在欄位從哪裡來
——那兩種名稱中繼資料查不到，欄位改讀[指令碼裡的宣告](completion-columns.md#指令碼宣告的資料表)。
「怎麼安全地把整句換掉」與「換成什麼樣子」兩段都不必為它們重寫。

```text
INSERT INTO dbo.Cat_BookCopy
(
    CopyNo,
    Barcode,
    BranchId
)
VALUES
(
    '',      -- CopyNo - varchar(10)
    DEFAULT, -- Barcode - nvarchar(100)
    NULL     -- BranchId - int
)
```

```text
MERGE INTO dbo.Cat_BookCopy AS target
USING dbo.SourceTable AS source
    ON target.CopyId = source.CopyId
WHEN MATCHED AND 1 = 0 THEN
    UPDATE SET
        target.CopyNo = source.CopyNo,
        target.Barcode = source.Barcode
WHEN NOT MATCHED BY TARGET AND 1 = 0 THEN
    INSERT
    (
        CopyNo,
        Barcode
    )
    VALUES
    (
        source.CopyNo,
        source.Barcode
    );
```

```text
DECLARE @NewDueDate datetime2(7);
EXEC dbo.usp_Loan_Renew @LoanId = 0,                     -- int
                        @Days = 0,                       -- int，選擇性
                        @NewDueDate = @NewDueDate OUTPUT -- datetime2(7)
```

五種展開只有「換成什麼」與「換掉哪一段」不一樣，「怎麼安全地換」是同一份：
先把名稱插進去，用 `ITrackingSpan` 記住範圍，到背景取物件細節，回來確認原文
還在原處才替換。共用的那一份在 `Ssms22/Completion/SqlCommitExpander`，
各自的「換成什麼」在 `SqlCommitExpansions`。各寫一份的下場是其中一份少了一道，
而少的那一道會覆蓋使用者的輸入。

「換掉哪一段」只有兩個答案，由 `SqlCommitExpansionScope` 表示：上面四種從決定
目標的那個關鍵字起算（`ALTER`、`INSERT INTO`、`MERGE`、`EXEC`），函式的引數則只
蓋掉剛提交的那個名稱。後者非分開不可——`SELECT dbo.fn_DueDate` 那個位置根本沒有
「決定目標的關鍵字」，`TargetKeywordStart` 是 -1。

### 整句換掉時不能把使用者打的限定字換丟

兩種範圍都從**使用者自己打的限定字**起算，寫回去的名稱因此是緩衝區裡站著的那個
完整名稱（`SqlCompletionContext.QualifierStart` 給起點），不是只有剛插進去的那一段。
只拿插入的那一段重組的話，`INSERT INTO LibArchive.dbo.Loan` 會被換成
`INSERT INTO dbo.Loan`——語法完全正確，插進去的卻是**目前連線**裡同名的那一張表，
而畫面上看不出來。`MERGE` 與 `EXEC` 同理，三種跨資料庫的寫法本來就合法。

`ALTER` 是唯一的例外，而且反過來：T-SQL 只改得動目前這個資料庫裡的模組，
定義的標頭本來就是兩段式的。限定字指向別的資料庫或別台伺服器時整個不展開，
維持只插入名稱並記進診斷——把對面的定義貼進來會得到一句改到本地同名模組的敘述。



四種都**不**停在整段的結尾：定義動輒數十行，停在結尾等於一展開就被捲到最後一行，
使用者得自己捲回去才看得到剛剛選的是什麼。

`INSERT` 與 `EXEC` 停在**第一個要填的值**上——展開之後要做的第一件事就是填它。
`MERGE` 停在 `USING` 後面的來源資料表上，理由相同：三個子句都填好了，
唯一還沒填的就是它。
`ALTER` 沒有待填的值，停在標頭的**物件名稱之後**：讀一份既有定義是從名稱與參數
開始看的。位置由 `Core/Parsing/SqlModuleScript.FindHeaderNameEnd` 算，`PROCEDURE`、
`FUNCTION`、`TRIGGER`、`VIEW` 因此一次到齊——它只跳過物件種類那個詞元而不比對字面值，
比對就得維護一份清單，而漏掉一種的症狀是那一種物件安靜地退回停在結尾。

兩件事在那裡容易寫錯：位置必須在**改寫之後**的文字上算（`CREATE OR ALTER` 併成一個
`ALTER` 會讓後面每個字元往前位移，在原始定義上算出來的會落在名稱中間），而且標頭只切
定義的前 1024 個字元來找——這一段跑在 UI 執行緒上，為了三個詞元把整份切完是白付的代價。
名稱被超長的開頭註解推出那個視窗、或剛好被視窗切斷時才退回完整掃描。

### MERGE 的三條保守規則

MERGE 同時會改與插，展開出來的又是一句立刻執行得動的語句，所以三個地方刻意保守
（`Core/Statements/SqlMergeStatementText`）：

| 規則 | 不這樣做會怎樣 |
|---|---|
| 比對鍵取**主索引鍵**；沒有主索引鍵時留 `KeyColumn` 這個編譯不過的佔位字 | 猜一個欄位當鍵不會報錯，只會把資料寫到別列去 |
| 兩個動作子句都帶著 `AND 1 = 0` | 一次誤按 F5 就是一次資料事故 |
| `UPDATE SET` 不含比對鍵；整張表都是鍵時整個 `WHEN MATCHED` 就不寫 | 空的 `SET` 是語法錯誤，而更新鍵本身沒有意義 |

比對鍵**不**過濾 `CanInsert`：IDENTITY 的主索引鍵插不進去，但它正是最該拿來比對
的那一欄。欄位清單則照 `INSERT` 那一份的規則排除四種插不進去的欄位，
一個欄位都撈不到時整個放棄、維持只插入名稱——理由與 `INSERT` 完全相同。

展開出來的 `target` 與 `source` 兩個別名解析得回那兩張表，所以接著改條件時
`target.` 與 `source.` 照樣列得出欄位。那條鏈以前由片段的欄位格守著，
現在守在 `SqlMergeStatementTextTests.展開出來的別名解析得回資料表`。

### 只要名稱的時候按一次復原

`INSERT INTO t SELECT …` 與照順序傳值的 `EXEC p 1, 2` 都是常見寫法，那些時候展開
反而礙事。插入名稱與展開是**兩次獨立的編輯**，所以按一次 `Ctrl+Z` 就退回只有名稱的
狀態，不是退回打到一半的前綴。

刻意不做成「Tab 展開、Enter 只插入名稱」：`SqlAssistCompletionCommandHandler` 沒有
接管清單的 Tab 與 Enter，那兩個鍵由平台處理；自己攔一個處理常式記下按了哪個鍵也
不可靠——本擴充與平台的處理常式都排在 `default` 之前，彼此的先後順序沒有保證。
不想要展開的人另有五個開關（`ALTER`、`INSERT`、`MERGE`、`EXEC`、函式引數各一），
全部在設定的「插入與展開」頁，見 [settings.md](settings.md)。
