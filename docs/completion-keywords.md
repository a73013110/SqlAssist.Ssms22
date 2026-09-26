# 關鍵字目錄與位置分層

本頁只處理 T-SQL 關鍵字的產生、位置旗標與資料庫物件過濾；子句回溯的邊界另見
[子句邊界與不開清單](completion-boundaries.md)，SET 選項這類非保留字見[子句片語](completion-phrases.md)。

## 產生與維護

清單裡的 191 個 T-SQL 關鍵字不是手寫的，由 `tools/Generate-Keywords.ps1` 反射
SSMS 自帶的 ScriptDom 產生，結果 commit 進 `Core/Keywords/SqlKeywordCatalog.Generated.cs`。
換 SSMS 版本重跑一次就更新。每個階段都自我驗證，不猜任何一個字：

1. **取字面值**：列舉 `TSqlTokenType` 的成員名稱，大寫後丟回 tokenizer，
   token 型別對得回原成員才採用。標點與字面值（`Comma`、`HexLiteral`…）自然
   對不回來所以被排除；名稱含 camelCase 轉折的再試一次補底線的寫法，
   撈回 `CURRENT_TIMESTAMP`、`IDENTITY_INSERT`、`TRY_CONVERT` 這一類。

2. **定位置**：把每個關鍵字塞進樣板的洞裡剖析，依錯誤碼判定它在該位置合不合法。
   46005（必須是 X 卻發現 Y）、46010（語法不正確）、46014（只可存在於資料行層級）
   = 不合法；46029（未預期的檔案結尾）= 合法，只是語句沒寫完。少算 46005 的話任何名稱
   都「接受」；少算 46014 的話 `DEFAULT` 會被分到 `CREATE TABLE t (` 的開頭。
   單一續尾會誤判——`BACKUP ` 之後是檔案結尾、`SELECT ` 之後卻是語法錯誤，
   兩者都合法——所以每個位置試一組續尾取聯集。非保留字要以**關鍵字身分**過才算
   屬於那個位置：同一組續尾換成普通名稱也過的話，那一次只證明它能當名字。

3. **寫完一項或一句的字**：某個樣板接上它就是完整的一句。語法樹裡以它結尾的是語句以外的
   片段（`NULL`、`DESC`）時，之後與識別字之後相同，往回找子句；是語句本身（`BREAK`、
   `COMMIT`）時，它是[語句界線](completion-boundaries.md#語句的界線)的一種，而那一句再也接不了
   語句開頭以外的字時，之後才是語句開頭——`COMMIT` 還接 `TRAN`、`RETURN` 還接運算式、
   `BEGIN TRAN` 還接變數，照舊 `Any`。

手寫的只有每個位置的樣板，關鍵字的分類全部由剖析器決定。樣板必須是分析器判得出、
而且回報含該位置的文字：樣板表隨產物輸出成 `SqlKeywordCatalogData.Templates`，
由 Core 測試逐條回驗。只有產生器分得出的位置是自欺——型別寫完之後（`CREATE TABLE t (a int |`）因此沒有樣板，是 `Any`。

非保留字是唯一的例外：`THROW`、`APPLY`、`NOLOCK` 這些在文法上不是關鍵字，
ScriptDom 的 token 列舉沒有它們——
任何工具在這一塊都只能自己維護清單。產生器裡的 `$NonReservedSupplement` 就是
那份清單，內容刻意等於「舊的手寫清單裡有、但 ScriptDom 認不得」的 11 個字，
位置一樣自動分類。

### 依位置分層

191 個字全部無條件列出來的話，打第一個字元時清單會被文法上根本不可能出現的字
塞滿。因此每個關鍵字都帶著「可以出現在哪些位置」，由
`SqlKeywordPositionAnalyzer` 判斷游標當下在哪個位置後過濾：

```text
（語句開頭）          → SELECT、USE、BACKUP、RESTORE、CREATE…
SELECT * FROM t ORDER BY    → CASE、CONVERT、COALESCE…
SELECT * FROM t ORDER BY a  → ASC、DESC
SELECT * FROM t GROUP BY a  → HAVING、ORDER（GroupByTail，不接 ASC）
SELECT TOP 10         → PERCENT、WITH，以及選取清單起點的字（TopClauseTail）
CREATE                → TABLE、VIEW、PROCEDURE…
SELECT * FROM t WHERE → EXISTS、NOT、CASE…
SET NOCOUNT           → ON、OFF（SetOptionValue；片語比對不上時的退路）
BEGIN … END           → 下一句的字，加上 ELSE、TRY、CATCH（BlockEnd）
IF @a = 1 SELECT 1    → 選取清單尾端，加上 ELSE（IfBodyEnd）
DECLARE c CURSOR LOCAL → FOR（CursorOption；選項由片語給）
ALTER TABLE t         → ADD、ALTER、DROP、CHECK、NOCHECK、SET、WITH、MERGE
ALTER TABLE t ADD     → CONSTRAINT、DEFAULT、PRIMARY、FOREIGN、UNIQUE、CHECK、INDEX…
CREATE TABLE t (      → CONSTRAINT、PRIMARY、UNIQUE、INDEX…，沒有 DEFAULT（ColumnDefinition）
```

位置切在「游標前一個詞元」之後，因為那正是分析器認得的粒度——它分不出
`FROM t ` 的 `t` 是資料表還是聯結對象，目錄就不假裝分得出來。

#### 判不出位置的字只在判不出位置時出現

產生器判不出位置的深層子句字（`FILLFACTOR`、`STOPLIST`…）產出為 `None`，`None` 只有
這一個意思：分析器也判不出位置（`Any`）時才出現。規則在 `SqlKeywordPositionExtensions.Allows`，
關鍵字、內建函式與片段共用。放行到每個位置的話 `SELECT F` 列得出 `FILLFACTOR`；
整個藏起來也不行，判不出位置的地方（`WITH (`、`= ANY`）正是它們的用處。
某個字的用法落在判得出的位置時，該補的是產生器的樣板，不是放寬這一條。
「這一格是新名字」是另一軸（`SqlCompletionSlot`），不借用 `None`。

#### `Any` 是給「判不出來」用的，不是給「不想判」用的

`Any` 含所有位元，而過濾是 `positions & 目前位置`，所以**回一次 `Any` 等於
191 個關鍵字與 49 筆片段全部進場**。分析器判得出來卻回 `Any` 的地方，症狀量得出來
——同一組候選、同一個前綴 `C`：

| 位置 | 回報 | 候選數 | 前幾名 |
|---|---|---|---|
| `SELECT C` | `SelectList` | 61 | `cs`，接著就是欄位 |
| `ORDER BY C`（修正前） | `Any` | 118 | 捷徑以 `c` 開頭的 13 筆片段全包，欄位掉到第 14 |
| `ORDER BY C`（修正後） | `OrderByColumn` | 30 | `cs`，接著就是欄位 |

因此 `OrderByColumn`（`ORDER BY`／`GROUP BY` 要的那個欄位，含逗號之後的下一項）與
`AlterTableAction`／`AlterTableAdd`／`AlterTableColumn`、`BlockEnd`、`IfBodyEnd`、`CursorOption`
都是**自己的成員**，不借用 `Any`。欄位**之後**是 `OrderByTail`（`ASC`／`DESC`）與 `GroupByTail`（`HAVING`），三者不能混。

`ALTER TABLE` 那三個位置認的是「往回正好是 `ALTER TABLE` 加一個名稱單位」，緊鄰的形狀走不出
這一句。名稱單位含點號（`dbo.t` 是一個），與別名判斷共用 `SqlTokenNavigator.SkipQualifiedNameBackward`。

#### 位置過濾也管資料庫物件

關鍵字、內建函式與片段各自帶著位置旗標，**名稱沒有**——資料表與程序是執行期從
中繼資料來的，帶不了旗標。所以反過來列寫不出名稱的位置（`SqlKeywordPositionExtensions.AcceptsNames`），
每一項都要說得出「那裡沒有任何名稱是合法的」：

| 位置 | 那裡只接受 |
|---|---|
| 子句尾端（`GROUP BY a \|`、`WHERE a = 1 \|`、`FROM t a \|`） | 運算子或關鍵字；別名是新名字 |
| `StatementStart`、`BlockStart`、`BlockEnd`、`IfBodyEnd`（`;`、`BEGIN`、區塊的 `END` 之後） | 下一句的關鍵字或 `ELSE` |
| `CursorOption`（`DECLARE c CURSOR LOCAL \|`） | 選項或 `FOR` |
| `ByAnchor`（`ORDER \|`、`GROUP \|`） | `BY` |
| `DdlObject`（`CREATE \|`、`ALTER \|`、`DROP \|`） | 物件**種類** |
| `AlterTableAction`、`AlterTableAdd`、`ColumnDefinition` | 動作、條件約束關鍵字，或新資料行名稱 |
| `SetOptionValue`（`SET NOCOUNT \|`） | `ON`、`OFF`（`SET IDENTITY_INSERT \|` 要資料表，不在此） |

子句尾端換行後補上的語句開頭照樣不接名稱：`FROM t a⏎CREA` 的欄位屬於上一句，
省略 EXEC 的程序呼叫又只在**批次第一句**合法（文件開頭或 `GO` 之後，`StartsBatch`）。
那裡只放行程序。

`InsertTarget` 刻意不在裡面：`INSERT dbo.Loan VALUES (…)` 是合法的 T-SQL，`INTO`
可以省略。`SetTarget` 也不在——`SET |` 與 `UPDATE t SET |` 是同一個位置，而後者要的是
資料行。`CaseArm`、`CaseBody` 不在：CASE 的各段寫的是運算式，欄位與函式都對。

判斷比的是「位置裡還有沒有別的位元」而不是位元交集：判不出位置時回傳的 `Any`
含著上表每一個旗標，用交集的話 fail-open 會變成 fail-closed，**每一個**位置的資料庫
物件都會消失。兩個方向都釘在 `SqlKeywordPositionTests.位置過濾也管資料庫物件`。
