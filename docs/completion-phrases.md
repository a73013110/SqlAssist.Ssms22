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
| `,*` | 標頭開的逗號清單，游標在逗號之後；只能是最後一項 |
| （空） | 沒有尾巴，只認位置：「這個位置接得了這些字」；必須寫 `After` |

候選字是關鍵字清單加上 ScriptDom 內部 `CodeGenerationSupporter` 的全部字串常數，
接不接得上用與第三階段相同的規則：普通名稱過不了而它過得了才算。比的除了整段，
還有「撐過字本身」：`ROWS BETWEEN UNBOUNDED` 要再接 `PRECEDING` 才完整，只比整段的話
它與普通名稱一起被拒。剖析器也有讀完才回頭驗的地方（`DECRYPTION BY CERTIFICATE KEY x` 在 `CERTIFICATE` 報錯）：
讓前面的字被拒過的字，要有續尾把整句寫完才算。
普通名稱在任何一組續尾整段都過不了的片語是**封閉**的；名稱後面還要再寫一段的
（`UPDATE t SET`、`OPEN SYMMETRIC KEY k DECRYPTION`）也會判成封閉，由 `Closed = $false` 宣告不封閉。

探測文字本身已是完整語句時（`CREATE INDEX i ON t (a) `），接得上的字也含下一句的開頭；
產生器扣掉在 `SELECT 1; ` 探到的那一份，被誤扣的（`WITH` 也是 CTE 的開頭）由更長的片語或 `Values` 補回。
這種片語帶 `EndsStatement`，游標換了行就不算數——那一格更可能是下一句。

- `Expand`：每個接得上的字接在後面成為新片語，直到那個字寫完語句。普通名稱放在同一格也完整時，完整的
  可能只是名稱讀法（`OPEN SYMMETRIC` 也是名叫 `SYMMETRIC` 的資料指標）：照樣往下，但扣掉普通名稱之後
  接得上的字（`FETCH NEXT ` 之後不列 `INTO`），扣完沒有字就不立。
  展開出來的片語字可以是零個：`SET ROWCOUNT ` 之後要數字，清單就該是空的。
- `Values`：剖析器把值當名稱看、分不出來時才手寫（`SET DATEFORMAT` 的 `dmy`）。
  每個值仍要剖析得過（接得上一組續尾，或開得了一組清單：索引鍵之後的 `WITH` 只接 `(`），
  過不了就中止產生；`Closed` 由人宣告那一格只有這幾個值。
- `Template`：用 `After` 位置的第幾個樣板探測：`IS` 要 `WHERE a `，代表樣板 `WHERE a = 1 ` 之後寫不出它。

### 片語裡的每一個字

寫得出 `CREATE OR ALTER`，`CREATE ` 之後就要有 `OR`。逐字探測問不出
這種字：`CREATE OR` 接任何續尾都在 `CREATE` 就報錯。所以產生器拿整段剖析得過的片語當證據，
把每一個字補進它前面那段；那段還不是片語就另立一個，條件是尾巴認得出來——以字面字結尾，
`Lead` 片語至少兩項。以名稱或值結尾的一段之後什麼都可能接，單獨的 `ON`、`NEXT` 到處比對得上。

語句說明的名稱與別名也是證據，只補進已有的片語（另立會封閉 `BEGIN ` 其餘的字）。`DBCC ` 之後
什麼都收、探不出字，名單只有說明；ScriptDom 的 DBCC 名單混著 `WRITEPAGE` 等內部命令，不用。

第一個字前面那段是位置，不是片語。那個位置有只認位置的片語就補進去；沒有、關鍵字目錄在那裡
也給不了（`AT` 不在 `SelectListTail`，`ENABLE` 不是關鍵字）時，另立**附加片語**：只認位置，
比對永遠是「可能」，只加字、不藏字——普通片語比對確定時，`SELECT a ` 之後就只剩 `AT`。
前面那段已有片語列得出這個字（`CREATE ` 之後的 `SYNONYM`）就不立。

新增一個片語只要加一行再重跑，執行期不必改。

### 前一格

同一條尾巴在不同位置是不同的意思：一句開頭的 `SET` 接工作階段選項，`UPDATE t SET` 接資料行；
查詢寫完的 `FOR` 接 `XML`、`JSON`、`BROWSE`，資料表之後多一個 `SYSTEM_TIME`。
所以每個片語交代它第一個字前面那一格，二選一：

- `After`：位置名稱，取自第三階段的樣板表，探測用每個位置的**第一個**樣板，
  執行期由同一個位置分析回驗；兩個都不寫就是 `StatementStart`。
  探到一樣結果的位置併成一個片語。
- `Lead`：前一格判不出位置、而尾巴本身就認得出意思（`WITH EXECUTE AS`、`NEXT VALUE FOR`）時，
  探測要墊的文字；執行期不看前一格。判得出來的一律寫 `After`，同一件事只由位置分析說一次。

會重複的格子（`CURSOR LOCAL FAST_FORWARD `、MERGE 的 `WHEN`、視窗框架、`WITH RESULT SETS (…)` 的兩層清單）
尾巴寫不出來，位置寫得出來：沒有尾巴的片語帶 `After`，探測文字就是那個位置的樣板；
`AS OBJECT` 這種下一個字由掛在位置上的片語給。位置見[關鍵字](completion-keywords.md)。

`FOR` 還分游標選項、觸發程序標頭與 `SYNONYM` 的物件種類。`CREATE USER {name}` 從語句開頭寫起，不掛在物件種類上：
`ALTER USER` 接別的字，確定的比對會藏掉它們。前一格判不出位置的（選取清單以外的 `NEXT VALUE`、
預設值條件約束、`NOT FOR REPLICATION`）才寫更長的 `Lead` 尾巴，由比對取項數多的分開。

### 清單片語

標頭開的逗號選項清單寫成 `ALTER USER {name} WITH ,*`，共用位置 `OptionItem`（各敘述選項不同，各佔位元不足）：
LOGIN、USER、應用程式角色、DBCC、RAISERROR、`FOR XML`／`FOR JSON`。分析器走訪清單、交出錨點，比對看錨點前的標頭。
標頭本身的片語給第一項；逗號之後以每種第一項接逗號探測取聯集：用過的選項剖析器不收第二次。
`()` 探測代入 `(a)`，對括號內容有要求的（RAISERROR）由 `Group` 指定。

標頭寫不成尾巴的仍各佔一個位置：

- 夾著長度不定的一段：EXEC 的參數（`WITH RESULT SETS (` 也靠 `ExecuteOption` 認 EXEC）、BACKUP／RESTORE
  的裝置清單、CREATE INDEX 的 `INCLUDE (…)` 與篩選 `WHERE`。「到語句開頭為止的任意詞元」這種標頭元素不採用：
  尾巴比對得問分析器語句開頭，每個 `WITH` 都付這份代價；探測也得每句另給代入文字（BACKUP 少了 `TO` 剖析不過）。
- 選項寫完還要回報位置（模組的 `AS`、觸發程序的 `FOR`）；`EXECUTE AS` 這類多字選項以位置為鍵，掛到共用位置會漏進每一份清單。
- 不以逗號分隔：游標選項。

## 執行期

`SqlKeywordPositionAnalyzer.Analyze` 順手比對片語，結果放在 `SqlCaretPosition.Phrase`。
同時比對得上時取項數多的：`ROWS BETWEEN UNBOUNDED ` 是那一條（接 `PRECEDING`），而不是框架 `AND`
之後的 `UNBOUNDED`（接 `FOLLOWING`）。

帶 `After` 的片語再問 `SqlKeywordPositionAnalyzer.PositionBefore`：前一格判得出而且對得上
才算，區塊開頭視同語句開頭（`BEGIN SET`），換行補上的語句開頭只給真的開頭（`UPDATE t⏎SET` 不是）。前一格判不出位置（`Any`）時比對結果是**可能**
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
照目標過濾的話 `EXEC AS` 的 `AS` 永遠列不出來；`OPEN ` 的目標是資料指標，`SYMMETRIC`、`MASTER` 也是這樣並列。

「可能」出現得越少，清單越準；它的來源是位置分析的 `Any`，該補的是分析器。模組標頭的
`AS` 之後（`CREATE PROCEDURE p AS⏎SET NOCOUNT `）、IF 條件、`DESC` 之後因此都判得出來。

## 刻意沒收的

- 單獨的 `CURRENT`：`WHERE CURRENT OF` 也是它，只收視窗框架裡的幾種前綴。
- `STRING_AGG(…) WITHIN `：剖析器把 `WITHIN` 當成欄位別名，探出來的是別名之後的字。
