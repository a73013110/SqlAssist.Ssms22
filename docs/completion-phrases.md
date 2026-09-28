# 子句片語

本頁處理 SET 選項、`ALTER INDEX` 動作、`FOR XML` 模式這類「只在某條尾巴之後出現」的字；
關鍵字目錄與位置旗標見[關鍵字](completion-keywords.md)。

## 為什麼要另一套

`QUOTED_IDENTIFIER`、`REBUILD`、`MATCHED` 在 ScriptDom 的詞法器眼中是識別字，
`TSqlTokenType` 沒有它們，關鍵字目錄撈不到。就算撈得到，位置旗標也切不到這麼細：
`SET NOCOUNT ` 與 `SET DATEFORMAT ` 在分析器眼中是同一個位置，前者要 `ON`／`OFF`，
後者要 `dmy`。

這些字也**不進**關鍵字目錄：`PATH`、`TIME`、`TYPE`、`STATUS` 是很常見的資料行名稱，
進了目錄就會被自動大寫，還會改變分析器對「這是不是關鍵字」的每一個判斷。

## 片語怎麼來

`tools/Generate-Keywords.ps1` 的第四階段。手寫的只有 `$ClausePhrases` 的尾巴：

| 寫法 | 意思 |
|---|---|
| `{name}` | 一個名稱單位，含點號；保留字（`ALTER DATABASE CURRENT`）與變數（`BACKUP DATABASE @db`）也算 |
| `{value}` | 數值、字串、變數或一整組括號 |
| `()` | 一整組括號 |
| `(*` | 還沒關上的左括號清單，游標在 `(` 或逗號之後；只能是最後一項 |
| （空） | 沒有尾巴，只認位置：「這個位置接得了這些字」；必須寫 `After` |

候選字是關鍵字清單加上 ScriptDom 內部 `CodeGenerationSupporter` 的全部字串常數，
接不接得上用與第三階段相同的規則：普通名稱過不了而它過得了才算。比的除了整段，
還有「撐過字本身」：`ROWS BETWEEN UNBOUNDED` 要再接 `PRECEDING` 才完整，只比整段的話
它與普通名稱一起被拒。普通名稱在任何一組續尾整段都過不了的片語是**封閉**的。

探測文字本身已是完整語句時（`CREATE INDEX i ON t (a) `），接得上的字也含下一句的開頭；
產生器扣掉在 `SELECT 1; ` 探到的那一份，被誤扣的（`WITH` 也是 CTE 的開頭）由更長的片語或 `Values` 補回。
這種片語帶 `EndsStatement`，游標換了行就不算數——那一格更可能是下一句。

一千九百個候選字乘上幾十組續尾，單執行緒要半小時，所以這一段由腳本內嵌的 C# 平行探測。

- `Expand`：每個接得上的字接在後面成為新片語，直到語句完整。`SET` 往下四層，
  所以 `SET TRANSACTION ISOLATION LEVEL READ ` 有自己的 `COMMITTED`／`UNCOMMITTED`。
  展開出來的片語字可以是零個：`SET ROWCOUNT ` 之後要數字，清單就該是空的。
- `Values`：剖析器把值當名稱看、分不出來時才手寫（`SET DATEFORMAT` 的 `dmy`）。
  每個值仍要剖析得過（接得上一組續尾，或開得了一組清單：索引鍵之後的 `WITH` 只接 `(`），
  過不了就中止產生；`Closed` 由人宣告那一格只有這幾個值。
- `Template`：用 `After` 位置的第幾個樣板探測。同一個位置的樣板接得上的字不一定相同：
  `IS` 要 `WHERE a `，代表樣板 `WHERE a = 1 ` 之後寫不出它。

### 片語裡的每一個字

寫得出 `CREATE OR ALTER`，`CREATE ` 之後就要有 `OR`、`CREATE OR ` 之後就要有 `ALTER`。逐字探測問不出
這種字：剖析器要看到整段才收，`CREATE OR` 接任何續尾都在 `CREATE` 就報錯。所以產生器反過來拿
整條片語當證據（整段剖析得過），把每一個字補進它前面那段；那段還不是片語就另立一個，條件是
尾巴認得出來——以字面字結尾，`Lead` 片語至少兩項。以名稱或值結尾的一段之後什麼都可能接，
單獨一個 `ON`、`NEXT` 執行期到處比對得上，立了都會封閉掉不相干的清單。
`SqlClausePhraseTests` 以同一個範圍逐字回驗。

第一個字前面那段是位置，不是片語。那個位置有只認位置的片語就補進去；沒有、關鍵字目錄在那裡
也給不了（`AT` 不在 `SelectListTail`，`ENABLE` 不是關鍵字）時，另立**附加片語**：只認位置，
比對永遠是「可能」，只把字加進清單、不藏別的字。換成只認位置的普通片語不行——比對確定時這一格的
關鍵字只來自片語，`SELECT a ` 之後就只剩 `AT`。前面那段已有片語列得出這個字（`CREATE ` 之後的
`SYNONYM`）就不立。附加片語輸出成另一個陣列 `AdditivePhrases`，回驗改問「比對回自己而且只是可能」。

新增一個片語只要加一行再重跑，執行期不必改。

### 前一格

同一條尾巴在不同位置是不同的意思：一句開頭的 `SET` 接工作階段選項，`UPDATE t SET` 接資料行；
查詢寫完的 `FOR` 接 `XML`、`JSON`、`BROWSE`，資料表之後多一個 `SYSTEM_TIME`。
所以每個片語交代它第一個字前面那一格，二選一：

- `After`：位置名稱，取自第三階段的樣板表，探測用每個位置的**第一個**樣板，
  執行期由同一個位置分析回驗；兩個都不寫就是 `StatementStart`。
  幾個位置探到一樣的結果時併成一個片語，不一樣的各自一個。
- `Lead`：前一格判不出位置、而尾巴本身就認得出意思（`WITH EXECUTE AS`、`NEXT VALUE FOR`）時，
  探測要墊的文字；執行期不看前一格。判得出來的一律寫 `After`，同一件事只由位置分析說一次。

會重複的格子尾巴寫不出來（`CURSOR LOCAL FAST_FORWARD `、`WITH COMPRESSION, `），位置寫得出來：
沒有尾巴的片語帶 `After`，探測文字就是那個位置的樣板。游標選項、觸發程序標頭、MERGE 的 `WHEN`、
BACKUP／RESTORE、模組、`EXEC` 與 `RAISERROR` 的 `WITH` 選項清單都是這樣；`WITH RESULT SETS (…)` 的
結果集與資料行定義是兩層括號清單，也各有位置（`ResultSetList`、`ResultSetColumn`、`ResultSetColumnTail`），
`AS OBJECT`、`NOT NULL` 的下一個字由掛在位置上的片語給；`OFFSET 10 ` 之後的
`ROWS`、視窗 `ORDER BY a ` 之後的框架、函式參數清單之後的 `RETURNS` 也是。位置見[關鍵字](completion-keywords.md)。

中間可以夾別的子句時也寫位置，不寫尾巴：CREATE INDEX 的 `WITH (` 前面可能是索引鍵、`INCLUDE (…)`
或篩選的 `WHERE`，每一種組合寫一條尾巴永遠寫不齊，位置分析認的是這一句（`IndexOption`）。
`FOR XML RAW, ` 的逗號之後、`GRANT … ON ` 之後同理。

第一個樣板是那個位置的代表寫法，而且是完整的語句，「寫到這裡已經完整」才判得準。其餘樣板是
第三階段撈齊關鍵字用的旁支：拿 `FROM t JOIN y` 探會長出 `FOR PATH` 之後一整串還缺 `ON` 的字，
時間也多好幾倍。

`FOR` 的意思也由前一格分開：查詢尾端、資料表、游標選項（`CursorOption`）、觸發程序標頭
（`TriggerHeader`）、`SYNONYM` 的物件種類。`CREATE USER {name}` 從語句開頭寫起，不掛在物件種類上：
`ALTER USER` 接的是別的字，確定的比對會把它們藏掉。前一格判不出位置的（選取清單以外的 `NEXT VALUE`、
預設值條件約束、`NOT FOR REPLICATION`）才寫更長的 `Lead` 尾巴，由比對取項數多的分開。

## 執行期

`SqlKeywordPositionAnalyzer.Analyze` 順手比對片語，結果放在 `SqlCaretPosition.Phrase`。
同時比對得上時取項數多的：`ROWS BETWEEN UNBOUNDED ` 是那一條（接 `PRECEDING`），而不是框架 `AND`
之後的 `UNBOUNDED`（接 `FOLLOWING`）。比對先依最後一個字分桶，每次按鍵只試同一桶與少數以佔位項結尾的片語。

帶 `After` 的片語再問 `SqlKeywordPositionAnalyzer.PositionBefore`：前一格判得出而且對得上
才算，區塊開頭視同語句開頭（`BEGIN SET`）。前一格判不出位置（`Any`）時比對結果是**可能**
（`SqlClausePhraseMatch.IsCertain` 為否）：片語的字加進整份關鍵字，同名的目錄字讓給片語，
清單不封閉。`PRINT @a SET ` 的前一格判不出來：那個意思可能根本不成立，當成確定並封閉的話
猜錯就少字——猜錯的代價必須是多幾個字。

沒有尾巴的片語前一格就是游標處的位置，只在尾巴都比對不到、或只比對到可能時才輪到；
游標處判不出位置時它什麼也沒認到，不算。附加片語也只認位置，但別的片語比對得上時讓給那一條。

比對**確定**時這一格的關鍵字只來自片語，規則在 `SuggestionContextFilter` 一處，
認的是建議項的 `Tag` 而不是文字——`READ` 同時在目錄與片語裡。

- 封閉：目標是 `ClauseKeyword`，清單只有片語的字，排在 `WITH (` 資料表提示之前判斷，
  所以 `CREATE INDEX … WITH (` 列的是索引選項。目標已收斂，`SqlCompletionPolicy` 不等字元數，
  `SET `、`CREATE ` 打完空白就開清單。
- 不封閉（`SET IDENTITY_INSERT ` 之後是資料表）：名稱照常，只有關鍵字換掉。

片語的字不看目標：目標說的是這一格要哪一種名稱，剖析器已證明片語的字接得上。`EXEC ` 的目標是程序，
照目標過濾的話 `EXEC AS` 的 `AS` 永遠列不出來。

「可能」出現得越少，清單越準；它的來源是位置分析的 `Any`，該補的是分析器。模組標頭的
`AS` 之後（`CREATE PROCEDURE p AS⏎SET NOCOUNT `）、IF 條件、`DESC` 之後因此都判得出來；
游標選項之後的 `FOR` 對上的是游標那一條，列的是 `SELECT`。

## 刻意沒收的

- 單獨的 `CURRENT`：`WHERE CURRENT OF` 也是它，只收視窗框架裡的幾種前綴。
- `STRING_AGG(…) WITHIN `：剖析器把 `WITHIN` 當成欄位別名，探出來的是別名之後的字。
- `DBCC` 的命令、`SET LANGUAGE` 的語言、`AT TIME ZONE` 的時區：剖析器收任何名稱，
  名單只在 `DbccCommand` 列舉或伺服器上（`sys.syslanguages`、`sys.time_zone_info`）。
