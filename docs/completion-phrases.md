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
| `{name}` | 一個名稱單位，含點號；保留字也算（`ALTER DATABASE CURRENT`） |
| `{value}` | 數值、字串、變數或一整組括號 |
| `()` | 一整組括號 |
| `(*` | 還沒關上的左括號清單，游標在 `(` 或逗號之後；只能是最後一項 |

候選字是關鍵字清單加上 ScriptDom 內部 `CodeGenerationSupporter` 的全部字串常數，
接不接得上用與第三階段相同的規則：普通名稱過不了而它過得了才算。比的除了整段，
還有「撐過字本身」：`ROWS BETWEEN UNBOUNDED` 要再接 `PRECEDING` 才完整，只比整段的話
它與普通名稱一起被拒。普通名稱在任何一組續尾整段都過不了的片語是**封閉**的。

探測文字本身已是完整語句時（`CREATE INDEX i ON t (a) `），接得上的字也含下一句的開頭；
產生器扣掉在 `SELECT 1; ` 探到的那一份，被誤扣的（`WITH` 也是 CTE 的開頭）以 `Values` 補回。
這種片語帶 `EndsStatement`，游標換了行就不算數——那一格更可能是下一句。

一千九百個候選字乘上幾十組續尾，單執行緒要半小時，所以這一段由腳本內嵌的 C# 平行探測。

- `Expand`：每個接得上的字接在後面成為新片語，直到語句完整。`SET` 往下四層，
  所以 `SET TRANSACTION ISOLATION LEVEL READ ` 有自己的 `COMMITTED`／`UNCOMMITTED`。
  展開出來的片語字可以是零個：`SET ROWCOUNT ` 之後要數字，清單就該是空的。
- `Values`：剖析器把值當名稱看、分不出來時才手寫（`SET DATEFORMAT` 的 `dmy`）。
  每個值仍要剖析得過，過不了就中止產生；`Closed` 由人宣告那一格只有這幾個值。

新增一個片語只要加一行再重跑，執行期不必改。

### 前一格

同一條尾巴在不同位置是不同的意思：一句開頭的 `SET` 接工作階段選項，`UPDATE t SET` 接資料行；
查詢寫完的 `FOR` 接 `XML`、`JSON`、`BROWSE`，資料表之後多一個 `SYSTEM_TIME`。
所以每個片語交代它第一個字前面那一格，二選一：

- `After`：位置名稱，取自第三階段的樣板表，探測用每個位置的**第一個**樣板，
  執行期由同一個位置分析回驗；兩個都不寫就是 `StatementStart`。
  幾個位置探到一樣的結果時併成一個片語，不一樣的各自一個。
- `Lead`：尾巴本身就認得出意思（`NOT MATCHED`、`WITH EXECUTE AS`），只是探測要墊文字；執行期不看前一格。

第一個樣板是那個位置的代表寫法，而且是完整的語句，「寫到這裡已經完整」才判得準。其餘樣板是
第三階段撈齊關鍵字用的旁支：拿 `FROM t JOIN y` 探會長出 `FOR PATH` 之後一整串還缺 `ON` 的字，
時間也多好幾倍。

`FOR` 前一格判不出位置的那幾種意思（觸發程序、游標、`NEXT VALUE`、預設值條件約束、
`CREATE USER`、`CREATE SYNONYM`、`NOT FOR REPLICATION`）各寫一條更長的尾巴，由比對取項數多的分開。

## 執行期

`SqlKeywordPositionAnalyzer.Analyze` 順手比對片語，結果放在 `SqlCaretPosition.Phrase`。
同時比對得上時取項數多的：`OFFSET 10 ROWS ` 是 `OFFSET {value} ROWS` 而不是視窗框架的
`ROWS`。比對先依最後一個字分桶，每次按鍵只試同一桶與少數以佔位項結尾的片語。

帶 `After` 的片語再問 `SqlKeywordPositionAnalyzer.PositionBefore`：前一格判得出而且對得上
才算，區塊開頭視同語句開頭（`BEGIN SET`）。前一格判不出位置（`Any`）時比對結果是**可能**
（`SqlClausePhraseMatch.IsCertain` 為否）：片語的字加進整份關鍵字，同名的目錄字讓給片語，
清單不封閉。`PRINT @a SET ` 的前一格判不出來：那個意思可能根本不成立，當成確定並封閉的話
猜錯就少字——猜錯的代價必須是多幾個字。

比對**確定**時這一格的關鍵字只來自片語，規則在 `SuggestionContextFilter` 一處，
認的是建議項的 `Tag` 而不是文字——`READ` 同時在目錄與片語裡。

- 封閉：目標是 `ClauseKeyword`，清單只有片語的字，排在 `WITH (` 資料表提示之前判斷，
  所以 `CREATE INDEX … WITH (` 列的是索引選項。目標已收斂，`SqlCompletionPolicy` 不等字元數，
  `SET `、`CREATE ` 打完空白就開清單。
- 不封閉（`SET IDENTITY_INSERT ` 之後是資料表）：名稱照常，只有關鍵字換掉。

「可能」出現得越少，清單越準；它的來源是位置分析的 `Any`，該補的是分析器。模組標頭的
`AS` 之後（`CREATE PROCEDURE p AS⏎SET NOCOUNT `）、IF 條件、`DESC` 之後因此都判得出來；
游標選項之後是 `CursorOption`，查詢的 `FOR` 對不上，`SELECT` 照列。

## 刻意沒收的

- `MERGE … WHEN ` 的 `MATCHED`：尾巴 `WHEN` 與 `CASE WHEN` 分不開。`NOT MATCHED`、
  `MATCHED BY` 之後分得開，有收。
- 單獨的 `CURRENT`：`WHERE CURRENT OF` 也是它，只收視窗框架裡的幾種前綴。
- `STRING_AGG(…) WITHIN `：剖析器把 `WITHIN` 當成欄位別名，探出來的是別名之後的字。
- 不在括號裡的選項清單（`BACKUP … WITH COMPRESSION, `、`CURSOR LOCAL `之後的第二個選項）：
  尾巴表達不了重複，而 `BACKUP` 與 `RESTORE` 的 `WITH` 前綴相同、選項不同。
- 觸發程序在選項之後的 FOR（`ON t WITH ENCRYPTION FOR `）：前一格判得出是資料來源尾端，
  會被當成查詢之後的 FOR。
- `DBCC` 的命令、`SET LANGUAGE` 的語言、`AT TIME ZONE` 的時區：剖析器收任何名稱，
  名單只在 `DbccCommand` 列舉或伺服器上（`sys.syslanguages`、`sys.time_zone_info`）。
