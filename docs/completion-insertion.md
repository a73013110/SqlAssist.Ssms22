# 建議項目的插入文字

本頁只處理提交時先寫入的名稱；後續改成整句見[展開範圍](statement-expansion.md)與
[展開內容](statement-values.md)。
欄位本身從哪裡查得到見 [completion-columns.md](completion-columns.md)，
清單怎麼排名與觸發見 [completion.md](completion.md)。

## 提交一個名稱時寫進去什麼

規則只有一份，在 `Core/Completion/SqlInsertionText`。它只吃建議項、上下文與設定，
三個都是純資料，所以整組情境都測得到（`tests/SqlAssist.Core.Tests/Completion/`）；
Ssms22 那一側只在建立項目與提交時各呼叫一次。

### 這串字只算一次

規則跑一次的地方也只有一處：建立建議項的時候（`SqlAsyncCompletionSource`）。
提交時交還給平台的那些由平台照 `CompletionItem.InsertText` 寫進去，自己接手的
那幾種（函式引數、整句展開、補右括號、接續建議）用的是同一串，不重算。

重算過，而症狀只在跨資料庫時看得出來：「`LibArchive.` 其實是資料庫而不是結構
描述」是中繼資料認出來的，[只認一次](qualified-names.md#右對齊猜錯時整條往左挪)；
提交那一端的上下文是從文字重新分析的，認不出這件事。於是同一筆建議交還給平台
時寫出 `LibArchive.dbo.SetPassWd`，自己接手時卻寫出 `LibArchive.SetPassWd`
——後者會被讀成「結構描述 `LibArchive`」，執行起來是「找不到資料行」。

### 有一半的建議根本不套這些規則

關鍵字、片段、欄位、內建函式、全域變數、變數、型別、參數、日期部分與兩種提示，
插入文字在建立建議時就定案了，這裡原樣送出：欄位帶著必要的別名限定
（`lr.ReaderId`）、內建函式帶著左括號（`COUNT(`）、參數帶著 ` = `——打出參數名稱
就是要做具名傳值。硬套物件規則的症狀很明確：把 `@@ROWCOUNT` 當成物件名稱去問
「要不要加方括號」，寫進編輯器的會是 `[@@ROWCOUNT]`。

### 補不補結構描述，看限定字停在哪一格

問的不是「有沒有限定字」，是**停在哪一格**。只看文字分不出 `dbo.`、`LibArchive.`
與 `LibMirror.`，那要問中繼資料；段位怎麼跟著挪由 `Core/Parsing/SqlObjectPath` 算，
見 [metadata.md](metadata.md)。

| 使用者已經打的 | 限定字停在 | 補不補 |
|---|---|---|
| `FROM ` | 沒有限定字 | 由「插入物件時補上結構描述名稱」決定 |
| `FROM dbo.`、`FROM LibArchive.dbo.` | 結構描述 | **不補** |
| `FROM LibArchive..` | 結構描述（空的中間段） | **不補** |
| `FROM LibArchive.` | 資料庫 | **一定補，而且不歸偏好管** |

最後一列是唯一凌駕設定的一條：`LibArchive.Loan` 是兩段式，會被讀成「結構描述
LibArchive」，而那個結構描述並不存在。關掉一個為了少打幾個字的偏好，不代表要產生
執行不了的語法——與方括號那條[同一個理由](settings.md)。中間兩列反過來：補了會
寫出四段式的 `LibArchive..dbo.Loan`，而使用者打的第二個點號正是在說「照預設解析」。

### 點號留給使用者自己打

結構描述、資料庫與連結伺服器是路徑的中間段，提交時只寫名稱本身。

曾經連點號一起寫進去，想省一個按鍵並順便接著開下一段。那不對：提交一筆建議的
意思是「我要這個名稱」，不是「我要繼續往下走」——選了資料庫想直接換行去寫別的、
或想手動打結構描述的人，都得先退掉一個他沒要求的字元。接續本來就有人做了：打出
點號會讓上下文整個換掉，`SqlCompletionTriggers` 因此重開清單，而那條路徑對每一段
都一樣，見 [completion-context.md](completion-context.md)。

同一條也擋住「把結構描述限定到自己身上」：這三類的 `SchemaName` 就是它們自己，
掉進上一節那條規則會寫出 `dbo.dbo`。

### 方括號加在哪些名稱上

關掉「插入物件時加上方括號」只代表不想看到多餘的括號，不是要產生無效語法：形狀
不合（含空白、開頭是數字）或名稱本身是保留字（`Order`、`User`）時仍然要包。開著
也不代表什麼都包得下去——暫存資料表與資料表變數不在它的管轄內，判斷在
`Core/Parsing/SqlIdentifier.IsScriptScoped`，理由見 [settings.md](settings.md)。

`SqlInsertionText.Quote` 是這條規則的唯一入口，`SELECT *` 展開與建立欄位建議都走
同一個方法；各自照設定再判斷一次的話，症狀是同一個欄位在清單上與展開後包法不同。
