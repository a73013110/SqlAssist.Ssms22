# 建議清單

輸入時彈出的建議清單：排名、觸發時機、上下文收斂，以及清單內容的來源
（T-SQL 關鍵字、內建函式、資料型別、全域變數、程式碼片段、資料庫物件，以及指令碼
自己宣告的 CTE、暫存資料表與變數）。程式碼片段的格式與管理見 [snippets.md](snippets.md)。

## 清單內容與排名

在查詢編輯器輸入第一個字元後立即顯示建議，內容包含 T-SQL 關鍵字、內建函式、
程式碼片段，以及目前連線資料庫的 Table、View、Procedure、Function、Synonym
與 Schema。資料型別、全域變數（`@@ROWCOUNT`…）與指令碼自己宣告的變數不混在這一份
裡——它們各自只出現在文法只接受它們的那個位置，見下。

排名採用 fzf v2 的 Smith-Waterman 變體，並針對 SQL 識別字調整字元分類：
底線、井號、小老鼠與點號都視為分隔符，分隔符與 camelCase 轉折後方的字元
取得詞首加成。因此輸入 `libr` 時 `Lib_Reader` 就會排在第一，不必打到 `lib_re`。
命中的字元會在清單中以粗體標示。

45 筆 Snippet 不再以固定最高類別加成塞滿清單：沒有前綴時排在欄位、關鍵字與常用物件
之後，危險片段直接隱藏；輸入從捷徑開頭命中時才恢復最高加成，純子序列命中則維持低分。
每筆另帶 `SqlKeywordPosition`，語句級 DDL 不會混進 SELECT 欄位位置。這三層規則都在
Core，避免原生清單與測試用排名走出不同結果。

```text
s     → 顯示 SELECT、SET、ssf 等關鍵字與 Snippet，以及符合的資料庫物件
libr  → Lib_Reader 排第一
Tab   → 提交選取項
```

使用 `↑`、`↓` 選擇，`Tab` 或 `Enter` 提交，`Esc` 關閉，也可以直接用滑鼠點選。

## 清單引擎

清單由**平台原生的非同步 IntelliSense** 呈現：定位、螢幕邊界、捲動、滑鼠操作
與佈景主題都由編輯器負責，與其他擴充套件共用同一個 session。

排名不交給平台：平台預設的比對器沒有詞首感知，接上去 `libr` 又會排不到
`Lib_Reader`。因此本擴充匯出自己的 `IAsyncCompletionItemManager`，
沿用同一套模糊比對分數，並把命中區段交給平台畫粗體。

## 與 SSMS 內建 IntelliSense 並存

SSMS 內建的 T-SQL IntelliSense 是舊版語言服務（MPF 的
`Microsoft.VisualStudio.Package.LanguageService`），由它自己的命令篩選器觸發，
不會因為有新版建議來源就讓位。兩份清單同時活著時，舊版會對著已經被換掉的狀態
算範圍，於是每退一格就跳一次「值未落在預期的範圍內。」或「並未將物件參考設定為
物件的執行個體。」。

**不要整個關掉它。** 它的總開關 `languages.sql.intelliSense.enableIntellisense`
底下用 `enableWhen` 掛著 `underlineErrors`（紅色錯誤波浪線）與 `autoOutlining`
——關掉總開關等於連錯誤檢查一起關掉，而錯誤檢查是這個擴充完全沒有提供的東西。
換到的是清單不打架，付出的是整份語法檢查，划不來。

要擋的只有「打字時自動彈出的那份清單」，而那是另一個旗標。預設開啟的
**「只使用 SqlAssist 的建議清單」**（`sqlAssist.suggestions.suppressNativeMemberList`）
把舊版語言服務的 `LANGPREFERENCES2.fAutoListMembers` 設成 0，其餘一切照舊。
實作與逐條理由見 `Ssms22/Settings/NativeMemberList`。

分得開是因為決定「要不要把清單畫出來」的那一行讀的就是它。
`Source.HandleCompletionResponse` 只在下列條件成立時才呼叫 `completionSet.Init`：

```text
AutoListMembers || reason == CompleteWord || reason == DisplayMemberList
```

因此關掉之後：打字不再彈出，`Ctrl+Space` 與 `Ctrl+J` 仍然叫得出舊版清單
（這一段是刻意留著的缺口，兩邊都會回應），而波浪線走的是另一條路——
`Source.OnIdle` 到 `BeginParse(ParseReason.Check)`——完全不看這個旗標。

順帶收掉的是 `RadLangSvc.Source.OnCommand` 裡「Backspace／Delete 時重新篩選舊版
清單」那一段：它只在清單顯示中才執行，而那正是刪字元時跳錯誤對話框的來源。

三件事讓這條路可靠：`HandleCompletionResponse` 是 internal 且非虛擬，RadLangSvc
覆寫不了；它的 `LanguagePreferences` 子類別（`SqlIntelliSenseSettings`）也沒有覆寫
`AutoListMembers`；而 `LanguagePreferences` 實作 `IVsTextManagerEvents2`，所以寫下去
立刻生效，不必重開查詢視窗——`enableIntellisense` 是在 `RadLangSvc.Source` 的建構式
裡抓進欄位的，那個才需要重開。

早期版本改用執行期硬關對方 session 的做法，但在 SSMS 22 兩條管線共用同一條
命令鏈，整批關掉會連帶收掉自己剛觸發的那一個，反而讓清單完全不出現。
那條路徑已經移除。

## 關鍵字目錄

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

#### 往回找子句關鍵字時要認得的兩個結構

「最近的子句關鍵字」不是往回數詞元就找得到的，路上有兩個結構不認得就會判錯，
而且三種很常見的寫法都會踩到：

| 寫法 | 只數詞元會判成 | 症狀 |
|---|---|---|
| `FROM (SELECT … ON a = b) d ` | 走進子查詢，撈到內層的 `ON` | 打不出 `WHERE` |
| `SELECT a, ` | 選取清單的尾端 | 列出 `FROM`、`INTO`，`CASE` 反而不見 |
| `JOIN b ON b.x = a.x ` | 只有述詞的尾端 | 打不出 `WHERE`、`INNER` |

- **括號群組是一個完整的運算元**，往回走時整組跳過。裡面的子句屬於它自己，
  走進去撈到的是別人的答案。跳過的括號兩兩不重疊，所以整趟仍然是線性的；
  配對不起來時直接放行成 `Any`，不猜。
- **逗號代表清單再來一項**，位置回到清單的**起點**而不是尾端：`SELECT a, ` 與
  `SELECT ` 同一個位置，`FROM a, ` 與 `FROM ` 同一個位置。
- **`ON` 的述詞寫完之後是兩個位置的聯集**。JOIN 條件後面同時還能接 `AND`、`OR`
  （述詞尾端）與 `WHERE`、另一個 `JOIN`、`GROUP`（資料來源尾端）。位置本來就是
  旗標，文法允許兩個就報兩個，不必挑一個猜。
- **`SET` 子句寫完之後也是兩個位置的聯集**，同一條規則的第二次。`UPDATE t SET a = 1 `
  之後接得了 `WHERE`、`FROM`、`OUTPUT`、`OPTION`，而那一整組字掛的是**資料來源尾端**
  ——只給述詞尾端的症狀就是 `UPDATE` 寫到一半打不出 `WHERE`，而那是這個語句最常打的
  下一個字。工作階段選項的 `SET NOCOUNT ON` 會拿到同一組位置，那是「位置分析看到
  `SET` 一律回報同一個位置」這個既有取捨的延伸，代價是清單多幾個字。

#### 使用者正在取名字的位置不開清單

有些位置文法上要的是**使用者自己取的名字**。那裡清單裡沒有一項會是對的，而彈出來
的唯一效果是他順手按下 Enter，剛打的 `a` 被換成 `ALTER PROCEDURE`，得按復原才救
得回來。因此這些位置整份不參與建議，少的只是幾個字母的補字：

| 位置 | 為什麼確定是名字 |
|---|---|
| `FROM (SELECT …) ` | 衍生資料表的別名是文法強制的，少了它就是語法錯誤 |
| `FROM t AS `、`SELECT x AS `、`JOIN (…) AS ` | `AS` 前面是一項寫完的運算式或資料來源 |
| `FROM CTE_TEST `、`JOIN dbo.T `（同一行） | 資料來源之後、別名還沒寫，見下 |
| `DECLARE @`、`CREATE PROCEDURE p @` | 使用者正在取的變數或參數名稱，見「變數與參數」 |

括號是什麼由它**前面**那個字決定，而不是由裡面裝什麼決定：同樣裝著一個 `SELECT`，
接在 `FROM`、`JOIN`、`APPLY`、`USING` 後面的是衍生資料表，接在 `IN`、`EXISTS`、`=`
後面的是運算式，後面不接別名。`FROM (t1 JOIN t2 ON …) ` 也不算——那是括號包起來的
聯結，別名反而不合法。

`AS` 同樣看**前面**，因為後面還沒打出來。它在 T-SQL 裡接兩種完全不同的東西，分不
出來的話兩邊都會壞：一邊是別名被清單換掉，另一邊是 `CREATE PROCEDURE p AS ` 的主體
開頭打不出 `BEGIN`。判斷的方式就是問「`AS` 那個位置本來是什麼位置」——選取清單尾端
與資料來源尾端代表「一項寫完了」，後面是別名；其餘一律照常，主體（`CREATE VIEW v AS`）
與執行身分（`EXECUTE AS`）都在其餘那一邊。比的是整個位置值相等而不是位元交集：
判不出位置時回傳的 `Any` 含著那兩個旗標，用交集的話 fail-open 就不 open 了。

`CAST(x AS ` 是這條規則唯一漏掉的一個：往回找子句關鍵字時會穿過那個還沒關上的
左括號撈到外層的 `SELECT`，於是判成選取清單尾端、也就是別名。它由「型別的位置」
那一條在更前面接走，見[資料型別](#資料型別)。

#### 沒有 AS 的別名靠換行分辨

`FROM dbo.PUBLISHER ` 之後直接打 `a` 也是別名，但這個位置文法上同時接得了 `WHERE`、
`INNER`、`ORDER`，而**打到一半的 `WHE` 與別名在剖析器眼中一模一樣**——文法給不出
答案，前綴也給不出（`a` 正好是 `AS`、`APPLY` 的前綴）。

唯一分得開的線索是**換行**：別名一定寫在資料來源的同一行，而子句與下一個敘述幾乎
總是換行寫。因此規則是「資料來源之後只有一個名稱單位，而且沒有換行」：

```text
FROM CTE_TEST |              → 別名的位置，不開清單
FROM dbo.PUBLISHER |          → 同上；帶點號的名稱算一個單位
FROM a INNER JOIN dbo.T |    → 同上
FROM CTE_TEST a |            → 兩個單位＝別名寫完了，INNER、WHERE 照常
FROM CTE_TEST AS a |         → 同上，AS 不算單位
FROM dbo.PUBLISHER ⏎          → 換行了，WHERE 與下一個 SELECT 照常
```

代價是「同一行、沒有別名」時打 `WHE`、`INN` 沒有清單。這是刻意換的：**打不出補字
的代價是多按兩下，別名被清單換掉的代價是按復原**，而且關鍵字轉大寫仍然會把 `where`
補成 `WHERE`，沒有任何字因此變得打不出來。換行之後（也就是幾乎所有人寫子句的方式）
一切照舊。

選取清單**不**比照辦理。`SELECT PublCode ` 之後同樣只有一個名稱單位，但那一行接著
要打的是 `FROM`——那是最常打的一個字，收掉它換來的問題比解決的大。

#### 沒有分號時，換行就是敘述邊界

T-SQL 的分號是**選用的**，所以敘述的結尾沒有任何詞元標示得出來：`WHERE a = 1` 之後
換行寫 `SELECT` 與換行寫 `AND`，在詞元串流上完全一樣。位置分析看到的只有上一句的
子句尾端，於是下一句的語句級片段（`ssf`…）一個都不會出現——而打了分號就有。
使用者看不出這兩者的差別，只會覺得片段時有時無。

線索與別名那條規則是同一個，只是方向相反：**子句已經寫完，而且游標換了行**，
就把 `StatementStart` 補進位置裡。

```text
UPDATE #Loan ⏎ SET CopyNo = 'C1' ⏎ WHERE ReaderId = 1 ⏎ |  → ssf、SELECT 照常
SELECT * FROM dbo.Loan ⏎ |                                  → 同上
SELECT * FROM dbo.Loan WHERE ReaderId = 1 |                 → 同一行，不補
SELECT CopyNo ⏎ |                                           → 選取清單不比照辦理
```

補的是**位元**而不是換掉一個：位置本來就是旗標，`AND`、`OR`、`ORDER` 這些續寫子句的
字一個都不能少。猜錯敘述邊界的代價必須是清單多幾個字，不能是少幾個字。

只認三個「子句已經寫完」的尾端——資料來源尾端、述詞尾端、`ORDER BY` 的欄位之後。
選取清單尾端不在裡面：`SELECT a` 換行之後幾乎總是接著寫下一個欄位或 `FROM`，
在那裡放進 64 個語句開頭的字與 35 筆片段，使用者真正要的欄位就被擠下去了。

#### 數值常值不開清單

`UPDATE t SET Fine = Fine - 10` 打到 `10` 的時候，位置分析幫不上忙：運算子之後一律是
`Any`，於是整個目錄進場，模糊比對把 `10` 對到 `LOG10`。使用者順手按下 Enter，
數字就變成了一個函式名稱。

判準是**詞元的第一個字元是不是數字**：T-SQL 的一般識別字不能以數字開頭，所以那個
詞元必然是一個數值常值，清單裡沒有一項會是對的。小數點與 `0x` 前置詞不必另外處理，
`1.5` 與 `0x1F` 拆出來的詞元一樣以數字開頭。

比的是第一個字元而不是「整個詞元含不含數字」：`Cat_BookCopy2` 這種名字很常見，
而暫存資料表的 `#` 也在詞元開頭。這與 `SqlCompletionTriggers` 不讓 `1.5` 的點號彈出
物件清單是同一條理由，只是那一條擋的是限定字。

## 內建函式

`COUNT`、`SUM`、`GETDATE`、`ISNULL`、`ROW_NUMBER` 這一類的內建函式也在清單裡，
提交時連左括號一起插入（`COUNT(`）——這些名稱單獨出現一律是語法錯誤，
補上括號等於少按一次鍵，而游標剛好停在第一個引數上。

這一份是**手寫**的，而且只能手寫。關鍵字目錄由產生器反射 ScriptDom 得到，
但內建函式在文法上不是關鍵字，`COUNT` 在 ScriptDom 眼中只是一個識別字，
token 列舉裡根本沒有它——任何工具在這一塊都只能自己維護清單。

與關鍵字重疊的名稱一律讓給關鍵字，而且是在執行期比對關鍵字目錄後排除，
不是靠人記得：`LEFT` 同時是 `LEFT JOIN` 與 `LEFT(字串, 長度)`，收進來會讓它
只剩運算式位置，`LEFT JOIN` 就從清單裡消失了。少一個函式只是少一個補字，
少一個 `JOIN` 是使用者打不出來。

清單裡**照樣寫著** `LEFT`、`RIGHT`、`CONVERT`、`COALESCE`、`NULLIF`、`TRY_CONVERT`
——它們就是內建函式，只是同時也是 ScriptDom 認得的關鍵字。哪些該讓開交給比對決定，
換一版 SSMS 之後這一組就會自己變。目前 118 個名稱裡有 6 個因此讓給關鍵字，
清單上剩 112 個。

位置與關鍵字走同一套分層，一律是運算式位置：語句開頭、資料來源位置與 DDL 物件
位置不該冒出 `COUNT`。`ALTER FUNCTION` 之後也不列——內建函式沒有定義可以改，
出現在那裡只會讓使用者選到一個改不了的東西。

自動大寫涵蓋內建函式，但**只在打出左括號時**：`max(` 得到 `MAX(`，`sum(`、
`count(`、`dateadd(` 同理。`max ` 與 `max,` 一個都不動——`year`、`month`、`day`、
`format` 這些名稱同時是很常見的資料行名稱，`SELECT year FROM t` 被改成
`SELECT YEAR FROM t` 是使用者沒有要求的改動，在 CS 定序的資料庫上還會把查詢改壞。
左括號是唯一分得開的依據：`max(` 在 T-SQL 裡只能是呼叫。

同時是內建型別的名稱（`char`、`nchar`）一個都不改：型別本來就不做自動大寫，
`CAST(x AS char(10))` 不該因為打了左括號就變成 `CHAR(10)`。

## 全域變數

打出 `@@` 就列出 T-SQL 的 32 個全域變數，右側寫著它是什麼：

```text
SELECT @@      → @@ROWCOUNT（上一個敘述影響的資料列數）、@@VERSION…
IF @@ERR       → @@ERROR
SET @rows = @@ROWC → @@ROWCOUNT
```

這一份與內建函式一樣只能手寫：它們在文法上是變數不是關鍵字，ScriptDom 的 token
列舉裡沒有它們。`@@REMSERVER` 刻意不收——它回報的遠端伺服器功能整個被拿掉了，
打出來也得不到有意義的值。

### 只在 `@@` 之後出現

全域變數不混進一般清單。它與下一節的變數是僅有的兩個由「正在輸入的詞元」而不是由
前導關鍵字決定的目標：`@@` 開頭的名稱在 T-SQL 裡只有這一種意思，前面是哪一個子句
都不改變這件事。反過來把 32 個 `@@` 塞進每一次按鍵的候選清單，只會讓真正要找的東西
更難找。

也因此這個位置不再判斷關鍵字位置：使用者已經打出 `@@` 了，此時再判一次，判對沒有
好處（清單本來就只剩這一類），判錯的代價是清單整個空掉。同樣的理由，它與 `USE`
之後的資料庫一樣跳過「輸入幾個字元之後才建議」——那兩個小老鼠已經把話說完了。

### 小老鼠算識別字的一部分

詞元起點必須落在**第一個**小老鼠上。切在 `ROW` 的話適用範圍只蓋住 `ROW`，
提交 `@@ROWCOUNT` 之後編輯器裡留下的是 `@@@@ROWCOUNT`。

分辨兩份清單的依據就是前綴本身，不是周圍的文法：兩個小老鼠開頭是系統的封閉清單，
一個是使用者自己寫的名字，見下。

## 變數與參數

打出 `@` 就列出這份指令碼在游標之前寫過的變數與參數，說明欄是宣告時寫的型別：

```text
DECLARE @readerId INT
SELECT @|              → @readerId（INT）
EXEC dbo.usp_Renew @|  → 同上
```

這與 CTE、暫存資料表是同一條推理：名稱只存在於這份指令碼裡，中繼資料一個都看不到，
而使用者會去補字正是因為那個名稱是他剛取的、還沒背起來。因此也與那一份一樣，
**不對資料庫送出任何查詢**，與「列出資料庫物件」的設定無關。

收的是每一個單小老鼠詞元，不分辨它出現在宣告還是使用的位置——理由同樣與暫存資料表
一致：`DECLARE`、程序參數、函式參數各認一次的話，漏掉的那一種寫法就會安靜地少一個
名稱，而多收的那些本來就是使用者自己打過的字。只有兩個限制：結束於游標之後的不收
（打到一半的 `@rea` 自己會出現在清單裡，而選它等於什麼都沒做），兩個小老鼠開頭的
不收（那是上面那份封閉清單）。

### 宣告的位置仍然不開清單

分辨的是「他在宣告」與「他在引用」：

| 位置 | 開不開 |
|---|---|
| `DECLARE @`、`DECLARE @a INT, @` | 不開，他正在取名字 |
| `CREATE PROCEDURE p @`、`ALTER PROCEDURE p @`、`CREATE FUNCTION f (@` | 同上 |
| `SET @`、`SELECT @`、`WHERE a = @`、`EXEC p @` | 開，他要的是上面宣告過的名稱 |

判斷方式是從那個小老鼠往回走到第一個關鍵字：是 `DECLARE`、`PROCEDURE`、`FUNCTION`、
`TABLE` 就是宣告，是別的關鍵字（`SET`、`WHERE`、`EXEC`…）就是引用。途中的括號整組
跳過，分號代表前一個敘述已經結束。`TABLE` 在裡面是為了 `DECLARE @t TABLE (…), @`
——跳過那組括號之後遇到的是 `TABLE` 而不是 `DECLARE`。

走到頭都沒有關鍵字時當成引用。這裡的 fail-open 換來的是「多列幾個他自己打過的名字」，
而反過來猜錯的代價是他打的名字被清單換掉。

### EXEC 的引數清單裡連參數一起列

```text
EXEC dbo.usp_Renew @|   → @readerId（int）、@days（int）…（那個程序的參數）
                          再接他自己宣告過的變數
```

參數與變數在這個位置都對——`EXEC p @readerId = 1` 是具名傳值，`EXEC p @myVar` 是照
順序傳一個變數——所以兩份併在一起，參數排前面：他打出小老鼠通常是為了具名傳值，
而那個名字是被呼叫端定的、比較不容易記得。這也是唯一一份會同時出現兩種類別的清單。

提交參數時連 ` = ` 一起寫進去，理由與內建函式補左括號相同。

「他在呼叫誰」由 Core 回答，參數清單由中繼資料層換：從小老鼠往回跳過已經打好的
引數，落點**必須剛好是** `EXEC`／`EXECUTE`。這比「往回找最近的 EXEC」嚴格，而嚴格
正是重點——中間只要夾著任何一個別的關鍵字，那就是另一個敘述：

```text
EXEC dbo.usp_Renew
SELECT * FROM dbo.Loan WHERE ReaderId = @|   → 不是那個 EXEC 的引數，只列變數
```

三段式的 `LibraryDb.dbo.usp_Renew` 取後兩段：中繼資料只看得到目前連線的資料庫。
`EXEC ('SELECT 1')` 與 `EXEC @procName` 讀不出名稱，直接放棄。

## 資料型別

`INT`、`NVARCHAR`、`DATETIME2` 這些名稱在文法上不是關鍵字——`SqlKeywordCatalog` 的
191 個字裡一個型別都沒有，理由與內建函式完全相同，ScriptDom 的 token 列舉撈不到它們。
因此這一份同樣是手寫的。

幾乎一定要寫長度或有效位數的型別提交時連左括號一起插入（`NVARCHAR(`），
與內建函式同一個道理。`DATETIME2`、`FLOAT` 不帶——用預設值的寫法遠比指定的常見，
補上去反而要多按一次刪除。

已淘汰的 `TEXT`、`NTEXT`、`IMAGE`、`TIMESTAMP` **收**，只在說明欄寫明替代品：
它們今天仍然運作，維護舊結構描述的人本來就要打出它們。這與全域變數排除
`@@REMSERVER` 不衝突——那個變數回報的功能整個被拿掉了，打出來也得不到有意義的值。
標準是「還有用就收，只是標清楚」。

### 六種看得出來的位置

判定成立時整份清單就只剩型別，關鍵字、片段與資料庫物件一個都不列——那些位置本來
就沒有別的東西是對的。也因為代價這麼直接（判錯就是那個位置什麼都打不出來），
只收看得出來的六種：

| 寫法 | 怎麼認出來 |
|---|---|
| `DECLARE @rows `、`DECLARE @a INT, @b `、`CREATE PROCEDURE p @x ` | 前一個詞元是落在宣告位置上的變數 |
| `RETURNS ` | 一個詞元就決定得了 |
| `CAST(x AS `、`TRY_CAST`、`PARSE`、`TRY_PARSE` | `AS` 而且還沒關上的那個左括號屬於這幾個函式 |
| `CONVERT(`、`TRY_CONVERT(` | 左括號前面是這兩個名字 |
| `CREATE TABLE t (Id `、`DECLARE @t TABLE (Id ` | 資料行名稱前面是左括號或逗號，而該左括號往回是 `TABLE` |
| `ALTER TABLE t ALTER COLUMN c ` | 前一個詞元是 `COLUMN` |

`CREATE TABLE` 的資料行清單是從**左括號**往回認的，不是從資料行名稱往回數：
`INSERT INTO t (col1, col2)` 的括號長得一模一樣，差別只在括號前面那個字是 `INTO`
的目標還是 `TABLE`。

「型別的位置」要排在「這裡不接受任何關鍵字」**之前**判斷。`CAST(x AS ` 在位置分析
眼中與 `SELECT x AS ` 的別名一模一樣——往回找子句關鍵字時會穿過那個還沒關上的左括號
撈到外層的 `SELECT`——順序反過來的話它會被別名那一條整份收掉。

沒有做成 `SqlKeywordPosition` 的一個新成員：那個列舉的每個成員都對應
`tools/Generate-Keywords.ps1` 裡的一個樣板，而型別根本不在關鍵字目錄裡，加一個沒有
樣板的成員只會讓兩邊對不起來。這裡要的是「換一份清單」而不是「篩掉一些關鍵字」，
那正是 `CompletionTarget` 的工作。

## 引數與提示的封閉清單

有三個位置除了固定那幾個字以外沒有別的東西是對的，它們與資料型別是同一種判斷、
同一個代價權衡——判定成立時整份清單就換掉，所以只收看得出來的：

```text
SELECT DATEADD(|              → DAY、MONTH、YEAR…（15 個日期部分）
SELECT * FROM dbo.Loan WITH (| → NOLOCK、UPDLOCK、INDEX(…（21 個資料表提示）
SELECT * FROM dbo.Loan OPTION (| → RECOMPILE、MAXDOP、FORCE ORDER…（17 個查詢提示）
```

三種都認得出來，是因為左括號**前面**那個字就把話說完了。CTE 的 `WITH` 不會誤判——
`;WITH c AS (` 的 `WITH` 與左括號之間隔著一個名稱。

日期部分只在**第一個**引數：打過逗號之後那裡要的是數字與日期。提示則是一份清單，
逗號之後還是提示。`INDEX` 提交時補左括號，理由與內建函式相同。

日期部分只收完整名稱，不收 `yy`、`dd` 這些縮寫：縮寫背得起來的人不需要補字，
而 15 個名稱再乘上兩三種縮寫，清單就從「一眼看完」變成要捲動。

已知會誤判的是別種 `WITH (…)`：`CREATE INDEX … WITH (FILLFACTOR = 80)` 與
`OPENJSON(…) WITH (col int '$.x')` 也會列出資料表提示。沒有為它們再加判斷，
是因為那兩個位置本來也沒有正確答案——前者要的是索引選項，後者要的是使用者自己取的
資料行名稱，換掉的只是一份同樣不對的關鍵字清單。

`SET NOCOUNT ON` 這一類的工作階段選項**沒有**收進來。位置分不開：位置分析看到
`SET` 一律回報同一個位置，而 `UPDATE t SET |` 要的是資料行，跟 `SET NOCOUNT` 完全
相反。要分開得往回找 `UPDATE`／`MERGE`，那條路的成本高過它省下的幾個字。

## 關鍵字自動大寫

打完 `select` 再按空白鍵就得到 `SELECT`，不必先按 Tab 提交建議。
`inner`、`join`、`on`、`desc` 等關鍵字同樣適用；觸發時機是任何無法構成識別字的
字元——空白、逗號、括號、分號與運算子。

刻意不做成「用空白鍵提交清單選取項」：清單當下選中的可能是別的東西，
那種做法會把使用者根本沒要的名稱寫進編輯器。這裡只改寫剛打完的那一個字，
與清單開不開著無關，結果完全可預測。

下列情形不動：已經是大寫、限定字後方的名稱（`dbo.select`）、變數（`@select`）、
字串與註解內、方括號與雙引號識別字內（`[select]` 是欄位名稱）。

`GO` 也不動——它是 SSMS 的批次分隔符而不是 T-SQL 關鍵字，而且兩個字母的字太容易
誤傷別名（`FROM Loans go` 是合法的寫法）。它仍然會出現在建議清單裡。

由 `工具 → SqlAssist → 關鍵字轉大寫` 開關控制；關掉它不影響清單裡的關鍵字建議。

## 依上下文縮小建議範圍

| 游標前方 | 只顯示 | 提交行為 |
|---|---|---|
| `FROM`、`JOIN`、`UPDATE`、`INTO`、`USING` | Table、View、資料表值函式 | 插入名稱；函式補上引數 |
| `CROSS APPLY`、`OUTER APPLY` | 資料表值函式 | 補上引數 |
| `INSERT INTO` | Table、View | 展開欄位清單與 `VALUES` |
| `MERGE`／`MERGE INTO` | Table、View | 展開比對鍵、`UPDATE SET`、`INSERT` 與 `VALUES` |
| `ALTER PROCEDURE`／`PROC` | Procedure | 展開完整 ALTER 定義 |
| `ALTER FUNCTION` | 兩種函式 | 展開完整 ALTER 定義 |
| `ALTER VIEW` | View | 展開完整 ALTER 定義 |
| `ALTER TRIGGER` | Trigger | 展開完整 ALTER 定義 |
| `DROP PROCEDURE`／`PROC`、`DROP FUNCTION`、`DROP VIEW` | 同上各一類 | 插入名稱 |
| 其餘位置選到自訂函式（`SELECT `、`WHERE `…） | — | 補上引數 |
| `DROP`、`DISABLE`、`ENABLE TRIGGER` | Trigger | 插入名稱 |
| `ALTER`／`DROP`／`TRUNCATE TABLE` | Table、View | 插入名稱 |
| `NEXT VALUE FOR`、`ALTER`／`DROP SEQUENCE` | Sequence | 插入名稱 |
| `EXEC`、`EXECUTE` | Procedure | 展開具名參數清單 |
| `CREATE`／`ALTER`／`DROP INDEX`／`STATISTICS`／`TRIGGER` 之後的 `ON` | Table、View | 插入名稱 |
| `USE` | 這台伺服器上的資料庫 | 插入名稱 |
| `dbo.`、`[dbo].` | 該結構描述的物件 | 插入名稱 |

`USING` 與 `FROM` 收在同一列不是為了湊數：MERGE 的來源與 FROM 的來源是同一條文法，
`SqlKeywordPositionAnalyzer` 與 `SqlScopeAnalyzer` 也早就這樣歸類。只有這一份漏掉時，
症狀是 `USING ` 之後完全沒有清單，而使用者看不出它和 `FROM ` 之後有什麼不同。

`IF EXISTS` 在比對前先剝掉一次，`DROP TABLE IF EXISTS `、`DROP TRIGGER IF EXISTS `
因此不必各寫一條加長版。剝除只砍尾端，前面每個詞元的位置都沒有位移，所以語句
關鍵字的起點仍然指得回原文；`IF EXISTS (SELECT …)` 那種流程控制剝完是空字串或
另一個語句的尾巴，兩者都推不出目標，與剝之前一樣不會有清單。

### `ON` 後面是資料表還是述詞

`ON` 在 T-SQL 裡是兩件完全不同的事，而分得開的線索在它**前面**：

```text
CREATE INDEX ix ON |          → 資料表；ON 前面是「名稱＋INDEX」
CREATE TRIGGER tr ON |        → 同上，TRIGGER
DROP INDEX ix ON |            → 同上
ALTER INDEX ALL ON |          → 同上（ALL 是關鍵字，但那一格仍然是索引的名稱）
CREATE STATISTICS st ON |     → 同上，STATISTICS
JOIN b ON |、MERGE … ON |     → 述詞；ON 前面是別名或 AS
GRANT SELECT ON |             → 述詞那一邊；ON 前面是關鍵字，不是名稱單位
CREATE INDEX ix ON t (a) ON | → 檔案群組；ON 前面是右括號
```

判斷刻意**只看 `ON` 前面那兩個名稱單位**，不往回走到敘述開頭：
「名稱之後是 `INDEX`／`STATISTICS`／`TRIGGER`」這個形狀只有 DDL 寫得出來，
而往回走要多認一整套邊界，還會把最後那個檔案群組的 `ON` 一起收進來。
`CREATE`／`ALTER`／`DROP` 三個動詞不必比：名稱後面接 `ON` 的物件只有那三種，
比不比對得到同一個答案，而漏掉 `CREATE OR ALTER TRIGGER` 的症狀是那裡安靜地沒有清單。

這份判斷有**兩個**呼叫端，共用 `Core/Parsing/SqlDdlTarget`：建議目標（列不列資料表）
與 `SqlScopeAnalyzer`（那張表算不算資料來源）。各寫一份的症狀是清單列得出資料表、
欄位卻一個都沒有——而那正是修正前的樣子：`cix` 的資料表格從來沒有清單，
資料行格列出來的是整個資料庫的資料表與預存程序。

反方向比正方向重要：把 JOIN 條件誤判成資料來源的話，`ON b.|` 會退回
「`b` 是結構描述」的解讀而完全列不出欄位，那是每天都會走到的路徑。
兩邊都釘在 `SqlDdlTargetTests`。

### MERGE 的動作子句

`WHEN MATCHED THEN UPDATE SET …`、`WHEN NOT MATCHED THEN INSERT …` 裡的
`UPDATE`／`INSERT`／`DELETE` 屬於同一個 MERGE，不是新敘述的開頭
（`SqlScopeAnalyzer.IsMergeAction`）。把它們當成邊界的話，游標一進到 `WHEN` 之後，
`target` 與 `source` 兩個別名就全部解析不出來——症狀是 `target.` 與 `source.`
都不再列欄位，而 `INSERT (` 連一個候選都沒有。

認的是**前一個詞元是不是 `THEN`**，不是「這份指令碼裡有沒有 MERGE」：一個 MERGE
之後接著獨立的 `UPDATE`，那個 `UPDATE` 仍然必須切斷範圍。T-SQL 裡 `THEN` 只出現在
CASE 與 MERGE，而 CASE 的 `THEN` 後面是運算式，不會是這三個關鍵字。

`INSERT (` 括號裡文法上只該有 target 的欄位，但範圍解析給的是整個 MERGE 的兩張表。
收斂成一張要另外記住「這個括號屬於 INSERT 子句」；多幾個選不中的名稱是多按幾下，
兩張表都不列的話那一格就完全沒有補字。
| `sys.`、`INFORMATION_SCHEMA.` | 目錄檢視、DMV 與系統程序 | 插入名稱 |
| `u.`（`u` 是敘述中的別名） | 該資料表的欄位 | 插入欄位名稱 |

`FROM`、`JOIN` 之後除了資料庫裡的資料表與檢視，還會列出**這份指令碼自己宣告的
資料來源**：CTE 與暫存資料表。中繼資料只看得到目前連線資料庫的 `sys.objects`，
CTE 只存在於指令碼裡，暫存資料表在 tempdb 裡，兩者一個都不在那份清單上——
而使用者會去 `FROM ` 後面補字，正是因為那個名稱是他剛取的、還沒背起來，
所以它們排在資料庫的資料表之前。

暫存資料表不分辨是哪一句建立的：井號開頭的識別字在 T-SQL 裡只有這一種意思，
而 `CREATE TABLE`、`SELECT INTO`、`INSERT INTO` 各認一次的話，漏掉的那一種寫法
就會安靜地少一個名稱。

### 資料表值函式與純量函式分開

`SuggestionKind.TableFunction` 與 `SuggestionKind.Function` 是兩類：前者是內嵌
（`IF`）與多語句（`TF`）資料表值函式，後者是純量函式。中繼資料層的
`SqlObjectKinds.IsDataSource` 早就這樣分了，只有建議項這一層曾經把三種函式壓成
同一類——症狀是 `FROM dbo.fn_` 之後整份清單一個函式都沒有，
而使用者看不出它和資料表有什麼不同。

分完之後三個位置各自對了：

- `FROM`、`JOIN`、`USING` 之後多列資料表值函式，純量函式仍然不列——
  它回傳的是一個值，放在那裡剖析不過。
- `APPLY` 之後**只**列資料表值函式。那個位置文法上要的是資料表值函式或衍生資料表，
  `CROSS APPLY dbo.Loan` 剖析得過卻沒有意義；而純量函式從前是連帶列出來的雜訊。
  認的是 `APPLY` 一個字，前面的 `CROSS` 與 `OUTER` 不改變後面要什麼。
- `ALTER FUNCTION`、`DROP FUNCTION` 之後兩種都列：兩種都改得動也刪得掉。

反過來也不能把資料表值函式併進 `Table`：那樣 `ALTER FUNCTION` 之後就列不出它們了。

`APPLY` 有自己的 `CompletionTarget` 而不是共用 `Function`，還有第二個好處：
`CompletionTarget.Function` 因此收斂成「`ALTER`／`DROP FUNCTION` 的那個名稱」，
提交時分得出「這裡要補引數」還是「這裡只要名稱」——見下面的函式引數。

### 系統物件只在兩個位置拉進來

`sys.objects`、`sys.dm_exec_requests`、`sp_executesql`、`sp_help` 這些原本一個都列不出來
——第一層查詢寫死 `is_ms_shipped = 0`，結構描述清單也明確排除了 `sys` 與
`INFORMATION_SCHEMA`。

它們現在有自己的查詢，而且**與第一層分開、只在被問到時才跑**：光是一個使用者資料庫
底下就有一兩千列，併進第一層等於每一次開啟查詢視窗都多付兩倍代價，換來的東西九成的
時間沒有人要。只有兩個位置會問：

- 使用者自己打出了 `sys.` 或 `INFORMATION_SCHEMA.`
- 游標在 `EXEC ` 之後——`sp_executesql`、`sp_help` 一律不加結構描述就呼叫

`ALTER PROCEDURE ` 不算，雖然它的目標同樣是預存程序：系統程序改不動，列出來只會讓
使用者選到一個改不了的東西，與內建函式不進 `ALTER FUNCTION` 是同一條理由。

這一份也刻意**不設有效期**：系統物件跟著 SQL Server 的版本走，不會在一次工作階段
中途變動，查一次就用到換連線為止。

`sys` 與 `INFORMATION_SCHEMA` 這兩個結構描述名稱則不必等中繼資料——它們在每一個
資料庫裡都存在，是產品事實而不是誰的 schema。少了這兩筆的話，使用者連「打 `sys`
再按 Tab」這條路都沒有。

`DROP` 家族與它們對稱：`DROP PROCEDURE`、`DROP FUNCTION`、`DROP VIEW` 之後同樣
只列那一類，但意圖是 `Reference`——那個位置要的只是一個名稱，把整份定義放進去
反而讓語句不合法。少寫哪一條都沒有徵兆，只是使用者在那個位置沒有清單。

觸發程序、序列與使用者自訂的資料表型別**只在自己的位置出現**，不進一般清單。
理由與全域變數同一條：`SELECT tr` 不該冒出觸發程序，而 `EXEC ` 之後選到一個觸發
程序一定執行失敗。觸發程序算模組（`OBJECT_DEFINITION` 拿得到定義），所以
`ALTER TRIGGER` 與 `ALTER PROCEDURE` 一樣直接展開完整定義。

這三種都不在 `sys.objects` 的原白名單裡，第一層查詢因此多收 `TR`、`TA`、`SO`，
並把 `sys.table_types` 另外 UNION 進來貼上 `TT` 標籤——與同義字的 `SN` 同一個做法。
資料表型別取的是 `type_table_object_id` 而不是 `user_type_id`：快取以 object_id 為鍵，
用型別自己的識別碼會與真的物件撞在一起，而那個 object_id 同時正好是它的欄位在
`sys.columns` 裡的鍵，於是欄位與滑鼠停留提示都不必另外接。

這一份不對資料庫送出任何查詢，因此與「列出資料庫物件」的設定無關；
也只在游標真的落在資料來源位置、而且沒有限定字時才掃——`FROM dbo.` 之後不該
出現沒有結構描述的名稱，而這條路徑在每一次按鍵上。

`ap` → `Tab` → 選取程序 → `Tab`，編輯器會直接放進該程序可執行的完整定義，
可以立刻修改並更新。定義開頭的 `CREATE` 或 `CREATE OR ALTER` 會改寫成 `ALTER`，
主體完全不動（主體裡的 `CREATE TABLE #tmp` 之類的語句不受影響），游標停在標頭的
物件名稱之後。`ALTER FUNCTION`、`ALTER VIEW`、`ALTER TRIGGER` 走的是同一份展開，
行為完全一致——那四種在 `SqlObjectKinds.IsModule` 裡是同一類，`OBJECT_DEFINITION`
都拿得到定義。

定義取不到只有兩個原因（物件是 `WITH ENCRYPTION` 建的，或這個登入沒有它的
`VIEW DEFINITION` 權限），這時維持只插入名稱，並在診斷紀錄裡寫明。

## 提交時展開成整句

有四個位置提交的不是一個名稱，而是一整句：`ALTER PROCEDURE` 之後放進完整定義，
`INSERT INTO` 之後放進欄位清單與 `VALUES`，`MERGE INTO` 之後放進比對鍵與兩個動作
子句，`EXEC` 之後放進具名傳值的參數清單。第五種只換掉剛插入的那個名稱，
見[函式的引數](#提交函式時補上引數)。

`INSERT INTO #Loan` 與 `INSERT INTO @rows` 走的是完全同一條路，差別只在欄位從哪裡來
——那兩種名稱中繼資料查不到，欄位改讀[指令碼裡的宣告](#指令碼宣告的資料表)。
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

### 提交函式時補上引數

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

### 只要名稱的時候按一次復原

`INSERT INTO t SELECT …` 與照順序傳值的 `EXEC p 1, 2` 都是常見寫法，那些時候展開
反而礙事。插入名稱與展開是**兩次獨立的編輯**，所以按一次 `Ctrl+Z` 就退回只有名稱的
狀態，不是退回打到一半的前綴。

刻意不做成「Tab 展開、Enter 只插入名稱」：`SqlAssistCompletionCommandHandler` 沒有
接管清單的 Tab 與 Enter，那兩個鍵由平台處理；自己攔一個處理常式記下按了哪個鍵也
不可靠——本擴充與平台的處理常式都排在 `default` 之前，彼此的先後順序沒有保證。
不想要展開的人另有四個開關（`INSERT`、`MERGE`、`EXEC`、函式引數各一），
見 [settings.md](settings.md)。

### 哪些欄位插不進去

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

### 值先填什麼

三條，順序不能對調：

1. 有 `DEFAULT` 條件約束 → `DEFAULT`
2. 可為 NULL → `NULL`
3. 其餘 → 依型別的預留值（`''`、`N''`、`0`、`0x`、`NEWID()`）

`VALUES (DEFAULT)` 對「沒有預設值而且 NOT NULL」的欄位是執行期錯誤，所以第一條
只能給真的有 DEFAULT 條件約束的欄位。

日期時間型別給 `NULL` 而不是 `''`：空字串轉成日期是 1900-01-01，那是一個**執行得動的
錯值**，而預留值要的正是「看得出來還沒填」。`NULL` 在 NOT NULL 的欄位上會失敗，
而失敗看得見。

### 參數的選擇性從定義讀，不從中繼資料讀

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

### 續行的對齊

`EXEC` 的續行對齊到第一個參數所在的欄，每一列的 `@` 因此落在同一個位置；
代價是名稱長的模組會把整段推向右邊。`INSERT` 的欄位與 `VALUES` 一律每列一個，
而且**不跟**「SELECT * 展開後的欄位排版」那個設定走——那個設定的三種排法都在權衡
「一行讀不讀得完」，而這裡的兩份清單是**成對**的：第三個欄位對第三個值，
攤成一行就對不起來，而對不起來的代價是把值填錯格。

縮排取語句所在行的前導空白，整段重複到每一行，定位字元原樣保留：每一行前面放的
都是同一串字元，在定位寬度不是 4 的機器上也對得齊。

`EXEC` 與 `EXECUTE` 照使用者原本寫的帶回去。統一改寫成 `EXEC` 也合法，但那是他
沒有要求的改動——與展開萬用字元時保留他自己寫的限定字是同一條。

### `INSERT INTO` 與單獨的 `INTO` 必須分開

`SELECT * INTO #tmp` 的 `INTO` 後面是一個**還不存在的新名稱**，在那裡展開骨架會蓋掉
使用者正在取的名字。所以認的是 `INSERT INTO` 這兩個字，而不是 `INTO` 一個字。

替換的範圍從 `INSERT` 開始，不是從 `INTO`——只從 `INTO` 開始換會在編輯器裡留下一個
孤零零的 `INSERT`。

## 欄位建議

輸入 `別名.` 或 `資料表名稱.` 時列出該資料來源的欄位，並顯示型別、NULL 與 PK。

別名解析需要看得到游標**後方**的文字：

```sql
SELECT u.| FROM dbo.Lib_Reader u
```

FROM 子句在游標之後，只看前文永遠解析不出 `u`——而編輯既有查詢正是最常
遇到這種情形的時候。因此上下文分析改用完整文字加游標位置的多載。

### 別名指向哪些欄位

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

### 指令碼宣告的資料表

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
資料表變數走的就是資料庫物件那一份展開，[排除規則](#哪些欄位插不進去)與
[值先填什麼](#值先填什麼)一個字都不必重寫。

### 只有開啟查詢的括號才切開範圍

範圍以括號界定，但**不是每一個括號都算**。判斷的依據是左括號後面接什麼：

| 寫法 | 是不是新範圍 |
|---|---|
| `IN (SELECT … FROM Child c)` | 是，子查詢自己帶 FROM 子句 |
| `FROM (SELECT …) d` | 是 |
| `COUNT(a.…)`、`ISNULL(a.…, 0)` | 否，只是函式引數 |
| `WHERE (a.… = 1)` | 否，只是運算優先權 |
| `IN (1, 2, 3)` | 否，只是清單 |
| `INSERT INTO t (…)` | 否，只是資料行清單 |

一開始每個括號都當成子查詢，症狀是彙總函式裡完全沒有建議：

```text
SELECT COUNT(a.| FROM dbo.PUBLISHER a
```

範圍只剩括號裡那一段，看不到 `FROM dbo.PUBLISHER a`，別名 `a` 解析不出來就退回
「`a` 是結構描述」的解讀——而沒有任何物件屬於名為 `a` 的結構描述，
於是清單是**空的**。同一個原因也讓 `WHERE (a.`、`IN (a.`、`ISNULL(a.` 全都沒有欄位。

規則的兩半必須一起成立：分不出「開啟查詢的括號」與「運算式的括號」的話，
修好彙總函式就會弄壞子查詢，內層的別名會解析到外層的資料表去。

順帶的好處是 `INSERT INTO t (|)` 也列得出 `t` 的欄位——那個括號同樣不是子查詢，
而那正是使用者在那個位置要的東西。

### 詞元一結束就把清單重開

平台的規則是「沒有 session 就問建議來源要不要開，已經有 session 就只重新篩選」。
這對識別字是對的：多打一個字母只是把候選變少。但**結束詞元的字元**不是——
它會讓上下文整個換掉，而還開著的那份清單是照舊上下文組出來的：

```text
SELECT a       → 清單開著，裡面是關鍵字與資料庫物件
SELECT a.      → 平台拿 a. 去比對同一份清單，一個都比不中，清單默默關掉
SELECT a.N     → 這時才重新問來源，欄位清單終於出現
```

因此輸入這類字元時自己把 session 收掉再開一次。判斷放在 `SqlCompletionTriggers`，
只看文字不碰編輯器，可以完整單元測試：

- 前一個字元還能構成識別字（`SELECT CUST|`）→ 不重開，平台自己的篩選是對的。
- **小老鼠除外**：它構得成識別字（`@@ROW` 的詞元起點必須落在第一個小老鼠上），
  但打出來的那一刻目標會整個換掉。`INSERT INTO ` 開著的是資料表清單，
  `INSERT INTO @` 要的是使用者自己宣告的變數，兩份沒有一項重疊——症狀是
  「單打一個 `@` 什麼都沒有，`@S` 才有提示」。`@@` 同理，那又是另一份封閉清單。
  判斷與呼叫端共用 `SqlCompletionTriggers.MayChangeContext`：一邊放行、
  另一邊擋掉，等於沒改。
- 有限定字（`a.`、`dbo.`、`[dbo].`）→ 重開。
- 前方關鍵字已經指定了物件類別（`FROM `、`JOIN `、`EXEC `、`USE `、
  `ALTER PROCEDURE `）→ 重開。少了這一條，`SELECT * FROM |` 也要再多打一個字母
  才列得出資料表——與點號完全同一個病。
- 其餘（`SELECT `、`COUNT(`、`SELECT a.X, `）→ 不重開。

最後一條不是遺漏，是「輸入幾個字元之後才開始建議」那個設定在說話：前綴是空的，
而觸發字元數最少是 1，建議來源本來就不會參與。在這裡先擋掉只是省下一次白跑。

小數點也不算：`12.` 的點號前面是數字而不是識別字，分不出來的代價是每次輸入
小數點都彈出整個資料庫的物件清單。

### 重開清單的三個步驟

片段接續與分隔字元走的是同一段程式（`SqlCompletionReopen`），三個步驟一個都不能少：

```csharp
broker.GetSession(view)?.Dismiss();                  // 1
var session = broker.TriggerCompletion(view, trigger, caret, token);   // 2
session?.OpenOrUpdate(trigger, caret, token);        // 3
```

1. **先收掉舊的。** `TriggerCompletion` 一開頭就先問 `GetSession`，只要還有 session
   就原封不動把它交回來——不先收掉，整個呼叫沒有任何作用。`Dismiss` 是同步的，
   回傳之前就已經把自己從 broker 的紀錄裡拿掉。
2. **`TriggerCompletion` 只是建立 session**：問過各個來源要不要參與、算出適用範圍，
   然後就結束了。
3. **`OpenOrUpdate` 才會去要清單並把 UI 畫出來。** 少了這一行，前面每一步都算對了，
   畫面上仍然什麼都不會出現。平台自己的命令處理常式在同一個位置也是這樣接著寫的。

整段排在派送佇列的 Background 優先權上執行，不在原地直接呼叫：提交當下平台正要
把 session 收掉，而輸入字元當下那個字元還沒進緩衝區——在原地開出來的清單，
看到的是上一個狀態。

緊接在 `FROM`、`JOIN`、`EXEC` 之後的限定字一律當結構描述：
`FROM dbo.` 要列出 dbo 的物件，而 `FROM u.` 這種寫法並不存在。

沒有限定字的位置（`SELECT |`、`WHERE |`、`ON |`）也會列出敘述看得到的欄位，
而且排在資料庫物件之前——在這些位置要的幾乎都是欄位。這裡走的是同一個
解析器，所以子查詢、CTE、暫存資料表與資料表變數的欄位一樣列得出來；解析不出來的
那一個來源跳過，其他來源照列——與限定字的位置不同，這裡少列一個來源的欄位
不影響其他來源的正確性。

敘述裡有兩個以上**相異的限定字**時，插入的文字會自動補上別名，否則
`SELECT Name FROM A a JOIN B b` 會因為欄位名稱模稜兩可而執行失敗。
數的是相異的限定字而不是來源數量：`FROM (SELECT Id, * FROM T t) d` 攤平出兩個
來源，但它們都叫 `d`。

這條路徑只使用**已經在快取裡**的欄位，不會為了列清單去等一次查詢；沒命中就
這一輪不顯示，背景預先載入補上之後下一次按鍵就有了。指令碼裡讀得出來的欄位
不必等任何東西，一律當場就有。

### 一次詞法分析，兩個答案

「敘述看得到哪些欄位來源」與「限定字指向哪些欄位」由同一次分析算出來，
一起掛在上下文上。呼叫端各自再掃一次同一份文字的話，每按一鍵就要多剖析
整份指令碼一遍。

同一個理由，`InitializeCompletion` 與提交路徑改用只吃游標前文的多載：
前者要的是適用範圍與要不要參與，後者要的是限定字與 `ALTER` 的關鍵字起點，
沒有一項需要看游標後方。全文分析只留在真的需要解析別名的那一次。
