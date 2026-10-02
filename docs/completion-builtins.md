# 內建函式與資料型別

這一份只管「清單裡有哪些名稱」。停在名稱上看到的用途與範例見
[內建說明](builtin-help.md)。

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
換一版 SSMS 之後這一組就會自己變。目前 194 個名稱裡有 6 個因此讓給關鍵字，
清單上剩 188 個。

位置與關鍵字走同一套分層，是運算式位置：語句開頭、資料來源位置與 DDL 物件
位置不該冒出 `COUNT`。`OPENJSON` 反過來只在資料來源位置，見[物件種類](completion-object-kinds.md)。`ALTER FUNCTION` 之後也不列——內建函式沒有定義可以改，
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

SQL Server 2025 才有的 `JSON`、`VECTOR` 同樣照收、不看連線的版本：這份清單本來就不查資料庫，
而寫給新版的指令碼常在舊版的連線上編輯；版本寫在說明欄。`VECTOR` 一定要寫維度，帶左括號。

### 看得出來的位置

判定成立時整份清單就只剩型別，關鍵字、片段與資料庫物件一個都不列——那些位置本來
就沒有別的東西是對的。也因為代價這麼直接（判錯就是那個位置什麼都打不出來），
只收看得出來的幾種：

| 寫法 | 怎麼認出來 |
|---|---|
| `DECLARE @a INT = NULL, @b `、`CREATE PROCEDURE p @x int OUTPUT, @y `、`CREATE AGGREGATE g (@a ` | 前一個詞元是落在[宣告位置](completion-variables.md#宣告的位置仍然不開清單)上的變數 |
| `RETURNS ` | 一個詞元就決定得了 |
| `CAST(x AS `、`TRY_CAST`、`PARSE`、`TRY_PARSE` | `AS` 而且還沒關上的那個左括號屬於這幾個函式 |
| `CONVERT(`、`TRY_CONVERT(` | 左括號前面是這兩個名字 |
| `CREATE TABLE t (Id `、`DECLARE @t TABLE (Id `、`ALTER TABLE t ADD Id `、`WITH RESULT SETS ((Id ` | 名稱前面那一格是資料行定義的開頭（位置分析的 `ColumnDefinition`、`AlterTableAdd`、`ResultSetColumn`）；名稱可加方括號 |
| `ALTER TABLE t ALTER COLUMN c ` | 前兩個詞元是 `ALTER COLUMN`；`DROP COLUMN c` 之後不是 |
| `CREATE SEQUENCE s AS `、`CREATE TYPE t FROM ` | 以型別為底的物件，名稱之後的那個字 |

型別那一組的 `AS` 與「`AS` 之後是型別」同一條判斷，只是把 `AS` 放在還沒寫的那一格問
（`SqlDataTypePosition.AcceptsAs`）：`DECLARE @x `、`CREATE PROCEDURE p @x ` 的型別清單多列可省的 `AS`，
新資料行名稱之後多列計算資料行的 `AS`（`RESULT SETS` 沒有）；`CAST(@y ` 寫完運算元、還沒寫過 `AS`
時列它，不看目標與位置旗標。各認一份的症狀是 `SELECT CAST(@y ` 借別名的 `AS` 碰巧列得出來，
`SET @x = CAST(@y ` 就列不出來。

資料行定義的開頭是一條規則：左括號前面是一份資料列的定義標頭——資料表的名稱（`CREATE` 以 `TABLE` 結尾的種類，
含 `EXTERNAL TABLE`；`INSERT BULK t`）、資料表型別（`@t TABLE`、`AS TABLE`、CLR 的 `RETURNS TABLE`），或資料列集函式的
結構描述（`OPENJSON(@j) WITH (`、`OPENXML(…) WITH (`）。名稱的省略段（`LibArchive..t`、`..t`）由共用的名稱單位跳過。

新資料行名稱之後不自己認形狀，問位置分析名稱前面那一格：`INSERT INTO t (col1, col2)`
的括號長得與 `CREATE TABLE` 一模一樣，分得開兩者的判準（括號前面是不是 `TABLE`）只有
位置分析一份。以前這裡另寫一份，`CREATE TABLE` 認得、`ALTER TABLE t ADD` 就認不得。

「型別的位置」要排在「這裡不接受任何關鍵字」**之前**判斷。`CAST(x AS ` 在位置分析
眼中與 `SELECT x AS ` 的別名一模一樣——往回找子句關鍵字時會穿過那個還沒關上的左括號
撈到外層的 `SELECT`——順序反過來的話它會被別名那一條整份收掉。

沒有做成 `SqlKeywordPosition` 的一個新成員：那個列舉的每個成員都對應
產生器 `Data/PositionTemplates.cs` 裡的一個樣板，而型別根本不在關鍵字目錄裡，加一個沒有
樣板的成員只會讓兩邊對不起來。這裡要的是「換一份清單」而不是「篩掉一些關鍵字」，
那正是 `CompletionTarget` 的工作。
