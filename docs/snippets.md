# 程式碼片段

內建 45 筆 SQL Server 2016 SP1 以上可用的片段；由
`工具 → SqlAssist → 程式碼片段…` 增刪修，也可以從設定頁進入。

| 分類 | 捷徑 |
|---|---|
| SELECT | `ssf`、`st100`、`st1`、`ssc`、`sd` |
| DML | `ii`、`ui`、`df`、`mg` |
| DDL | `cdb`、`ctb`、`cv`、`cp`、`cf`、`ctf`、`cix`、`at`、`dt`、`ap`、`af`、`av`、`atr` |
| 流程控制／交易 | `be`、`bt`、`ct`、`rt`、`ife`、`ifne`、`wl`、`tc`、`cs`、`cur`、`trn` |
| 查詢子句／其他 | `ij`、`lj`、`rj`、`fj`、`cj`、`ca`、`oa`、`ob`、`gb`、`cte`、`sno`、`ptt` |

`cf` 是純量函式、`ctf` 是內嵌資料表值函式——要記的是這一組**對比**而不是縮寫，
所以只差「多回傳一張表」的那個 `t`。CASE 使用 `cs`、BEGIN…END 使用
`be`，都不占用同名的 T-SQL 關鍵字。`ui`、`df`、`mg` 與 `dt` 標成危險片段：
沒有輸入任何前綴時不主動顯示，輸入捷徑或按下 Snippet 分類仍找得到。

內建片段的識別碼統一使用 `builtin.<捷徑>`：`ctf` 是 `builtin.ctf`，`be` 是
`builtin.be`。識別碼與捷徑維持一致，避免內建定義與使用者 override 使用不同名稱。

## CREATE 模組片段的註解標頭

`cp`、`cv`、`cf`、`ctf` 展開時前面帶一份與 SSMS 內建範本同格式的註解標頭
（作者、建立日期、說明），值的欄位對齊在第 16 欄。四筆共用同一份字串，
守門的是 `SqlSnippetDefaultsTests.四筆CREATE模組片段共用同一份註解標頭`。

**刻意不做成 Tab Stop 欄位。** 做成欄位的話 Tab 順序會變成作者→日期→說明→物件
名稱，而最常走的那條路（`cp` → Tab → 打名字）就要多按三次；標頭多半是事後補的，
不該擋在主線上。日期同理不自動填：原生 Expansion Engine 沒有日期函式，
`SqlNativeSnippetXmlBuilder.GetExpansionFunction` 也回 `E_NOTIMPL`，要自動填得在
`SqlSnippetExpansion` 加一種「計算型預設值」讓原生與 `caret` 降級兩條路共用求值——
那是另一件事，分開做。

註解不影響下一格的清單：`SqlLexicalContext` 讓游標落在 `--` 之後時整份不參與，
而標頭之後的 `CREATE PROCEDURE ` 仍然推不出目標（那是使用者正要取的新名字）。
兩者都由 `新建物件的名稱欄位不主動開清單` 連帶守著——它分析的正是「標頭加上
`CREATE …`」這一整段。

## 半句話加接續建議

20 筆片段**刻意不是 Tab Stop**，而是 `caret` 加接續建議：插入單獨一行的半句話，
游標停在尾巴，接著由建議清單接手。

| 片段 | 插入 | 接著列出 |
|---|---|---|
| `ssf`、`st100`、`st1`、`ssc`、`sd` | `SELECT * FROM `、`SELECT TOP (100) * FROM `… | 資料表與檢視 |
| `ii` | `INSERT INTO ` | 資料表與檢視；提交時展開欄位清單與 `VALUES` |
| `mg` | `MERGE INTO ` | 資料表與檢視；提交時展開比對鍵、`UPDATE SET`、`INSERT` 與 `VALUES` |
| `ui`、`df` | `UPDATE `、`DELETE FROM ` | 資料表與檢視 |
| `ij`、`lj`、`rj`、`fj`、`cj` | `INNER JOIN `… | 資料表與檢視 |
| `ca`、`oa` | `CROSS APPLY `、`OUTER APPLY ` | 函式 |
| `ap`、`af`、`av`、`atr` | `ALTER PROCEDURE `、`ALTER FUNCTION `、`ALTER VIEW `、`ALTER TRIGGER ` | 程序、函式、檢視、觸發程序；提交時放進完整定義 |

它們要填的是資料表、程序與函式的**真實名稱**，那份清單來自連線的中繼資料；
換成 `[dbo].[TableName]` 這種靜態欄位等於把這個擴充最核心的東西換掉。`ii` 與
`ap` 家族更是整條鏈的起點——選到資料表或模組之後由 `SqlCommitExpander` 放進可執行的
整句（見[整句展開](statement-expansion.md)）。四筆 `ALTER` 走的是同一份展開，因為
檢視與觸發程序在 `SqlObjectKinds.IsModule` 裡與程序、函式同一類。要 CREATE 的骨架請用 `cp`、`cf`、
`ctf`，那三筆才是 Tab Stop。

`mg` 也在這一族，而它是從 Tab Stop 換過來的。舊樣板把比對鍵、更新欄位與新增欄位
拆成六格，一次只填得了一個欄位，而 `INSERT (…)` 那一格的欄位與
`VALUES (source.…)` 那一格又是分開的——十個欄位就是二十次 Tab，正是使用者
「只能手動慢慢打欄位」的那句話。改成與 `ii` 同一條鏈之後，選好目標資料表就由
`SqlMergeStatementText` 依中繼資料一次填滿三個子句，唯一還要填的是來源資料表，
游標就停在那裡。展開的規則（比對鍵取主索引鍵、`AND 1 = 0` 閘門、`UPDATE SET`
不含鍵）見[整句展開](statement-expansion.md)。

**接得下去的條件是「展開出來的那一行結尾剛好是一個會列出東西的關鍵字」**，
因為接續清單的內容由 `SqlCompletionContextAnalyzer` 從游標前一個詞元推出來。
尾巴多一個分號、括號或換行都會讓下一步變成一般清單，而症狀只是「清單沒有跳
出來」，沒有任何錯誤。守門的是
`SqlSnippetDefaultsTests.接續片段展開後落在會列出該類物件的位置`，它連
`CompletionIntent` 一起比——`ii` 落在 `DataSource` 還不夠，要 `InsertStatement`
才會展開成欄位清單，退化成 `Reference` 的話只是把資料表名稱補上去。

單行也是刻意的。`ij`、`lj` 曾經連 `AS t` 與 `ON 1 = 1` 一起插進去，代價是每次
都要回頭刪掉猜錯的別名與條件。改成單行之後別名與 `ON` 要自己打——`ON` 有關鍵字
自動大寫接著，而別名那一格本來就不開清單（見[沒有 AS 的別名](completion-keyword-context.md#沒有-as-的別名靠換行分辨)）。

`sd` 展開的是 `SELECT DISTINCT * FROM `。`DISTINCT *` 通常不是最終要的，但選完
資料表之後把游標移到 `*` 按 Tab 就展開成完整欄位清單再挑，比 `[$column$]` 一次
只填得了一個欄位好用。

`ca`、`oa` 之後的清單會連純量函式一起列——中繼資料把三種函式對應到同一個
`SuggestionKind`，要分開得新增一種類別。多幾個選不中的名稱是多按幾下，而把整個
`CompletionTarget.Function` 讓掉的話那個位置就完全沒有補字。

Tab Stop 樣板一律**不寫方括號**。要不要加括號由「插入物件時加上方括號」
（`sqlAssist.insertion.useSquareBrackets`，預設關閉）決定，樣板自己寫死
`[dbo].[TableName]` 的話，同一份指令碼裡就會出現兩種風格，而那個差別使用者從來
沒有要求過。含空白或保留字的名稱仍然要自己補上括號。
