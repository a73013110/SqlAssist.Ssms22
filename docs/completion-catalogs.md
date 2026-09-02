# 靜態目錄：關鍵字、內建函式、資料型別

清單裡不隨指令碼內容變動的三類項目：T-SQL 關鍵字、內建函式與資料型別。三者的目錄
從哪裡來、各自只在哪些位置出現、以及要增刪時該動哪一份。清單本身怎麼排名與觸發見
[completion.md](completion.md)。

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

