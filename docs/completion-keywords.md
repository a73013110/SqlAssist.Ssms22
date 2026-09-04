# 關鍵字目錄與位置分層

本頁只處理 T-SQL 關鍵字的產生、位置旗標與資料庫物件過濾；子句回溯的邊界另見
[關鍵字上下文](completion-keyword-context.md)。

## 產生與維護

清單裡的 191 個 T-SQL 關鍵字不是手寫的，由 `tools/Generate-Keywords.ps1` 反射
SSMS 自帶的 ScriptDom 產生，結果 commit 進 `Core/Keywords/SqlKeywordCatalog.Generated.cs`。
換 SSMS 版本重跑一次就更新。兩個階段都自我驗證，不猜任何一個字：

1. **取字面值**：列舉 `TSqlTokenType` 的成員名稱，大寫後丟回 tokenizer，
   token 型別對得回原成員才採用。標點與字面值（`Comma`、`HexLiteral`…）自然
   對不回來所以被排除；名稱含 camelCase 轉折的再試一次補底線的寫法，
   撈回 `CURRENT_TIMESTAMP`、`IDENTITY_INSERT`、`TRY_CONVERT` 這一類。
   242 個成員得到 180 個保留字。

2. **定位置**：把每個關鍵字塞進樣板的洞裡剖析，依錯誤碼判定它在該位置合不合法
   （46010 語法不正確 = 不合法；46029 未預期的檔案結尾 = 合法，只是語句沒寫完）。
   單一續尾會誤判——`BACKUP ` 之後是檔案結尾、`SELECT ` 之後卻是語法錯誤，
   兩者都合法——所以每個位置試一組續尾取聯集。

手寫的只有 16 個上下文位置、合計 24 個樣板片段，191 個關鍵字的分類全部由剖析器決定。

非保留字是唯一的例外：`THROW`、`APPLY`、`NOLOCK` 這些在文法上不是關鍵字，
ScriptDom 的 token 列舉沒有它們，SqlParser 的 Scanner 也一律回報識別字——
任何工具在這一塊都只能自己維護清單。產生器裡的 `$NonReservedSupplement` 就是
那份清單，內容刻意等於「舊的手寫清單裡有、但 ScriptDom 認不得」的 11 個字，
位置一樣自動分類。

### 依位置分層

191 個字全部無條件列出來的話，打第一個字元時清單會被文法上根本不可能出現的字
塞滿。因此每個關鍵字都帶著「可以出現在哪些位置」，由
`SqlKeywordPositionAnalyzer` 判斷游標當下在哪個位置後過濾：

```text
（語句開頭）          → SELECT、USE、BACKUP、RESTORE、CREATE…（64 個）
SELECT * FROM t ORDER BY    → CASE、CONVERT、COALESCE…（28 個）
SELECT * FROM t ORDER BY a  → ASC、DESC
CREATE                → TABLE、VIEW、PROCEDURE…（33 個）
SELECT * FROM t WHERE → EXISTS、NOT、CASE…（33 個）
ALTER TABLE t         → ADD、ALTER、DROP、CHECK、NOCHECK、SET、WITH、MERGE
ALTER TABLE t ADD     → CONSTRAINT、DEFAULT、PRIMARY、FOREIGN、UNIQUE、CHECK、INDEX…
```

位置切在「游標前一個詞元」之後，因為那正是分析器認得的粒度——它分不出
`FROM t ` 的 `t` 是資料表還是聯結對象，目錄就不假裝分得出來。
產生器判不出位置的 24 個深層子句字（`FILLFACTOR`、`STOPLIST`…）一律放行：
分不出位置的代價是清單多幾個字，猜錯位置的代價是使用者永遠打不出來。

#### `Any` 是給「判不出來」用的，不是給「不想判」用的

`Any` 含所有位元，而過濾是 `positions & 目前位置`，所以**回一次 `Any` 等於
191 個關鍵字與 45 筆片段全部進場**。分析器判得出來卻回 `Any` 的地方，症狀量得出來
——同一組候選、同一個前綴 `C`：

| 位置 | 回報 | 候選數 | 前幾名 |
|---|---|---|---|
| `SELECT C` | `SelectList` | 61 | `cs`，接著就是欄位 |
| `ORDER BY C`（修正前） | `Any` | 118 | 捷徑以 `c` 開頭的 13 筆片段全包，欄位掉到第 14 |
| `ORDER BY C`（修正後） | `OrderByColumn` | 30 | `cs`，接著就是欄位 |
| `ALTER TABLE t ADD C`（修正前） | `Any` | 118 | 同上 |
| `ALTER TABLE t ADD C`（修正後） | `AlterTableAdd` | 24 | 欄位、`CHECK`、`CONSTRAINT` |

因此 `OrderByColumn`（`ORDER BY`／`GROUP BY` 要的那個欄位，含逗號之後的下一項）與
`AlterTableAction`／`AlterTableAdd`／`AlterTableColumn` 都是**自己的成員**，
不再借用 `Any`。`OrderByTail` 是欄位**之後**的 `ASC`／`DESC`，兩者不能混。

`ALTER TABLE` 那三個位置認的是「往回正好是 `ALTER TABLE` 加一個名稱單位」，
不是「這份指令碼裡有沒有 `ALTER TABLE`」——理由與 `SqlScopeAnalyzer.IsMergeAction`
相同，接在後面的獨立敘述不屬於它。名稱單位含點號（`dbo.t` 是一個不是兩個），
那份走訪與別名判斷共用 `SqlTokenNavigator.SkipQualifiedNameBackward`。

`ALTER TABLE t ALTER` 在 ScriptDom 眼中直接是語法錯誤（它要看到 `COLUMN` 才收），
所以產生器的續尾清單多了 `COLUMN x int` 一條；少了它，`ALTER` 就分不到
`AlterTableAction`，而那個字正是那個位置最常打的。續尾取聯集，多一條只會讓分類
更寬鬆。

#### 位置過濾也管資料庫物件

關鍵字、內建函式與片段各自帶著位置旗標，**名稱沒有**——資料表與程序是執行期從
中繼資料來的，帶不了旗標。所以反過來列「哪些位置一個名稱都不接受」，而那份清單
短得多，每一項都要說得出「那裡沒有任何名稱是合法的」：

| 位置 | 那裡只接受 |
|---|---|
| `ByAnchor`（`ORDER \|`、`GROUP \|`） | `BY` |
| `DdlObject`（`CREATE \|`、`ALTER \|`、`DROP \|`） | 物件**種類** |
| `AlterTableAction`（`ALTER TABLE t \|`） | `ADD`、`ALTER`、`DROP`、`CHECK`… |
| `AlterTableAdd`（`ALTER TABLE t ADD \|`） | 條件約束關鍵字，或使用者正要取的新資料行名稱 |

`InsertTarget` 刻意不在裡面：`INSERT dbo.Loan VALUES (…)` 是合法的 T-SQL，`INTO`
可以省略。`SetTarget` 也不在——位置分析看到 `SET` 一律回報同一個位置，而
`UPDATE t SET |` 要的是資料行。`ColumnDefinition`、`CaseArm`、`CaseBody` 不在的理由
不一樣：那三個位置目前只有**產生器**認得，分析器一次都回不出來
（`CREATE TABLE t (a int |` 回的是 `Any`），列進來只是宣告一件不會發生的事。

判斷比的是「位置裡還有沒有別的位元」而不是位元交集，理由與 `AS` 那條規則完全相同：
判不出位置時回傳的 `Any` 含著上表每一個旗標，用交集的話 fail-open 會變成
fail-closed，**每一個**位置的資料庫物件都會消失——那比原本的雜訊嚴重得多。
兩個方向都釘在 `SqlKeywordPositionTests.位置過濾也管資料庫物件`。
