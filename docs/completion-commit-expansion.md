# 提交時展開成整句

展開的規則、排版、值先填什麼，以及哪些欄位不能插進去。欄位本身從哪裡查得到見
[completion-columns.md](completion-columns.md)，清單怎麼排名與觸發見
[completion.md](completion.md)。

有四個位置提交的不是一個名稱，而是一整句：`ALTER PROCEDURE` 之後放進完整定義，
`INSERT INTO` 之後放進欄位清單與 `VALUES`，`MERGE INTO` 之後放進比對鍵與兩個動作
子句，`EXEC` 之後放進具名傳值的參數清單。第五種只換掉剛插入的那個名稱，
見[函式的引數](#提交函式時補上引數)。

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
蓋掉剛插入的名稱。後者非分開不可——`SELECT dbo.fn_DueDate` 那個位置根本沒有
「決定目標的關鍵字」，`TargetKeywordStart` 是 -1。

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

## MERGE 的三條保守規則

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

## 提交函式時補上引數

括號在 T-SQL 裡不是選擇性的：`SELECT dbo.fn_DueDate` 不是「呼叫但沒傳引數」，
而是一個語法錯誤；沒有參數的函式也一樣要寫 `()`。所以提交一個使用者自訂函式時
一併補上整組引數，依參數型別填預留值：

```text
SELECT dbo.fn_DueDate(NULL)
FROM dbo.fn_LoansByReader(0, NULL, N'')
```

排版與 `EXEC` 正好相反，因為 T-SQL 對兩者的要求相反：`EXEC` 收具名傳值，
所以那一支每個參數一列、對齊 `@`、在右邊註明型別，有預設值的參數還可以整列刪掉；
函式只收**位置**引數，`dbo.fn_DueDate(@days = 1)` 不合法，有預設值的參數也不能省略
（省略的寫法是 `DEFAULT` 這個關鍵字，位置照留）。因此這一支排成一行、只有值，
連參數名稱都寫不進去——而它本來就常常出現在運算式中間，拆成多列會把使用者
正在寫的那句話撐開。型別看不到不是資訊遺失：滑鼠停留提示與浮動預覽本來就列著
整份參數清單，在編輯器裡再寫一次只是讓他多刪一次註解。

預留值與 `INSERT`／`EXEC` 共用同一份 `Core/Statements/SqlLiteralDefaults`：
數值 `0`、字串 `''`／`N''`、日期 `NULL`、`uniqueidentifier` 是 `NEWID()`。
各寫一份的下場是其中一份給日期填了空字串，而那會安靜地存進 1900-01-01。

要不要補由**被選中的東西**決定，不由位置決定——括號要不要寫跟前面是 `SELECT`
還是 `FROM` 無關。唯一的例外是 `ALTER`／`DROP FUNCTION`：那裡是宣告位置，
補上括號會讓那句 DDL 語法錯誤，所以擋的是 `CompletionTarget.Function` 這個目標。
`APPLY` 因此必須有自己的目標，否則兩種位置分不開。

參數一個都撈不到時**照樣**補上一對空括號，這與 `INSERT` 骨架「欄位撈不到就整個
放棄」相反，因為兩者的失敗長得不一樣：沒有欄位的 `INSERT` 是一句跑得動卻錯的話，
而沒有參數的函式呼叫本來就寫成 `()`——那是正確答案，不是半成品。
細節整個取不到（連不上、權限不足）時維持只插入名稱。

等待期間使用者自己打了左括號的話這一次就不補：追蹤範圍是 `EdgeExclusive`，
他打的那個字元落在範圍**外**，範圍裡的字一個都沒變，光看範圍內的文字看不出這件事，
補上去的結果會是 `dbo.fn_DueDate(NULL)(`。

T-SQL 的**內建**函式不走這條路：它們的左括號寫在建議項自己的插入文字裡
（`Core/Keywords/SqlFunctionCatalog`），那一份不查資料庫，也不受這個開關影響。

## 只要名稱的時候按一次復原

`INSERT INTO t SELECT …` 與照順序傳值的 `EXEC p 1, 2` 都是常見寫法，那些時候展開
反而礙事。插入名稱與展開是**兩次獨立的編輯**，所以按一次 `Ctrl+Z` 就退回只有名稱的
狀態，不是退回打到一半的前綴。

刻意不做成「Tab 展開、Enter 只插入名稱」：`SqlAssistCompletionCommandHandler` 沒有
接管清單的 Tab 與 Enter，那兩個鍵由平台處理；自己攔一個處理常式記下按了哪個鍵也
不可靠——本擴充與平台的處理常式都排在 `default` 之前，彼此的先後順序沒有保證。
不想要展開的人另有四個開關（`INSERT`、`MERGE`、`EXEC`、函式引數各一），
見 [settings.md](settings.md)。

## 哪些欄位插不進去

四種，漏掉任何一種的症狀相同——展開出來的 `INSERT` 一執行就錯：

| 排除 | 判斷依據 |
|---|---|
| `IDENTITY` | `sys.columns.is_identity` |
| 計算欄位 | `sys.columns.is_computed` |
| `rowversion`（舊名 `timestamp`） | 型別名稱；`sys.columns` 沒有這個旗標 |
| 時態與帳本資料表的 `GENERATED ALWAYS` 欄位 | `COLUMNPROPERTY(…, 'GeneratedAlwaysType')` |

最後一種刻意不讀 `sys.columns.generated_always_type`：那一欄要 SQL Server 2016 才有，
直接 SELECT 它會讓整份欄位查詢在更舊的執行個體上變成語法錯誤，而
`TryLoad` 會把它降級成「這一輪沒有資料」——於是欄位建議、萬用字元展開與結構預覽
會在那些伺服器上**一起安靜地消失**。`COLUMNPROPERTY` 對認不得的屬性名稱回傳 NULL，
舊版因此自然得到 0。

一個欄位都撈不到時**整個放棄**，維持只插入名稱：同義字在 `sys.columns` 裡沒有列，
組出 `INSERT INTO syn () VALUES ()` 比什麼都不做糟糕得多。這與 `SELECT *` 不做
部分展開是同一條理由。

## 值先填什麼

三條，順序不能對調：

1. 有 `DEFAULT` 條件約束 → `DEFAULT`
2. 可為 NULL → `NULL`
3. 其餘 → 依型別的預留值（`''`、`N''`、`0`、`0x`、`NEWID()`）

`VALUES (DEFAULT)` 對「沒有預設值而且 NOT NULL」的欄位是執行期錯誤，所以第一條
只能給真的有 DEFAULT 條件約束的欄位。

日期時間型別給 `NULL` 而不是 `''`：空字串轉成日期是 1900-01-01，那是一個**執行得動的
錯值**，而預留值要的正是「看得出來還沒填」。`NULL` 在 NOT NULL 的欄位上會失敗，
而失敗看得見。

## 參數的選擇性從定義讀，不從中繼資料讀

`sys.parameters.has_default_value` 對 T-SQL 模組**永遠是 0**——那一欄只對 CLR 模組
有效。所以「哪些參數可以省略」只能剖析 `OBJECT_DEFINITION` 的參數清單，
在 `Core/Statements/SqlModuleParameterDefaults`。

定義是第三層資料，本來不在按鍵路徑上；但提交也不在按鍵路徑上，而且
`GetDetailAsync` 的同一次呼叫本來就會把欄位、參數與定義一起帶回來，因此不多付
任何一次往返。讀不出來就少標幾個「選擇性」，不猜——猜錯會讓使用者刪掉一個其實
必填的參數。

`OUTPUT` 參數傳的必須是變數，光給字面值是語法錯誤，所以整段前面會補上 `DECLARE`。
使用者已經宣告過同名變數時會撞名，但那是一個當場看得見的編譯錯誤；少了 `DECLARE`
則是連編譯都過不了，那比什麼都不做糟糕。

沒有參數的模組不展開（展開起來與只插入名稱一模一樣）。擴充預存程序在
`sys.parameters` 裡也沒有列，同樣落在這一條。

## 續行的對齊

`EXEC` 的續行對齊到第一個參數所在的欄，每一列的 `@` 因此落在同一個位置；
代價是名稱長的模組會把整段推向右邊。`INSERT` 的欄位與 `VALUES` 一律每列一個，
而且**不跟**「SELECT * 展開後的欄位排版」那個設定走——那個設定的三種排法都在權衡
「一行讀不讀得完」，而這裡的兩份清單是**成對**的：第三個欄位對第三個值，
攤成一行就對不起來，而對不起來的代價是把值填錯格。

縮排取語句所在行的前導空白，整段重複到每一行，定位字元原樣保留：每一行前面放的
都是同一串字元，在定位寬度不是 4 的機器上也對得齊。

`EXEC` 與 `EXECUTE` 照使用者原本寫的帶回去。統一改寫成 `EXEC` 也合法，但那是他
沒有要求的改動——與展開萬用字元時保留他自己寫的限定字是同一條。

## `INSERT INTO` 與單獨的 `INTO` 必須分開

`SELECT * INTO #tmp` 的 `INTO` 後面是一個**還不存在的新名稱**，在那裡展開骨架會蓋掉
使用者正在取的名字。所以認的是 `INSERT INTO` 這兩個字，而不是 `INTO` 一個字。

替換的範圍從 `INSERT` 開始，不是從 `INTO`——只從 `INTO` 開始換會在編輯器裡留下一個
孤零零的 `INSERT`。

