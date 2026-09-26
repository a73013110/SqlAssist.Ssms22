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
| `^SET` | 第一個字必須是一句的開頭：`UPDATE t SET ` 的 SET 不算 |
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

## 執行期

`SqlKeywordPositionAnalyzer.Analyze` 順手比對片語，結果放在 `SqlCaretPosition.Phrase`。
同時比對得上時取項數多的：`OFFSET 10 ROWS ` 是 `OFFSET {value} ROWS` 而不是視窗框架的
`ROWS`。比對先依最後一個字分桶，每次按鍵只試同一桶與少數以佔位項結尾的片語。

比對到片語時**這一格的關鍵字只來自片語**，規則在 `SuggestionContextFilter` 一處，
認的是建議項的 `Tag` 而不是文字——`READ` 同時在目錄與片語裡。

- 封閉：目標是 `ClauseKeyword`，清單只有片語的字，排在 `WITH (` 資料表提示之前判斷，
  所以 `CREATE INDEX … WITH (` 列的是索引選項。目標已收斂，`SqlCompletionPolicy` 不等字元數，
  `SET `、`CREATE ` 打完空白就開清單。
- 不封閉（`SET IDENTITY_INSERT ` 之後是資料表）：名稱照常，只有關鍵字換掉。

`^` 用的是 `SqlKeywordPositionAnalyzer.StartsStatementAt`：前一個位置含語句或區塊開頭，
或子句寫完又換了行；判不出來算是，與位置分析 fail-open 同向。

## 刻意沒收的

- `MERGE … WHEN ` 的 `MATCHED`：尾巴 `WHEN` 與 `CASE WHEN` 分不開。`NOT MATCHED`、
  `MATCHED BY` 之後分得開，有收。
- 單獨的 `CURRENT`：`WHERE CURRENT OF` 也是它，只收視窗框架裡的幾種前綴。
- `STRING_AGG(…) WITHIN `：剖析器把 `WITHIN` 當成欄位別名，探出來的是別名之後的字。
- 不在括號裡的選項清單（`BACKUP … WITH COMPRESSION, `、`CURSOR LOCAL `之後的第二個選項）：
  尾巴表達不了重複，而 `BACKUP` 與 `RESTORE` 的 `WITH` 前綴相同、選項不同。
- `DBCC` 的命令、`SET LANGUAGE` 的語言、`AT TIME ZONE` 的時區：剖析器收任何名稱，
  名單只在 `DbccCommand` 列舉或伺服器上（`sys.syslanguages`、`sys.time_zone_info`）。
