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

產生器的第四階段（`PhraseExplorer`）拿剖析器探測，手寫的只有尾巴；寫法、展開與證據見[子句片語的產生器](phrase-generator.md)。

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
  `SET `、`CREATE ` 打完空白就開清單。片語那一格收變數時（`SET `、`FETCH c INTO `）清單也放指令碼的變數：
  打 `@` 只是篩選這份預開的清單，少了它們，前一格判得出位置的 `SET @`（`;`、`)`、`BEGIN` 之後）反而列不出變數。
  宣告的位置（`CREATE PROCEDURE p `）照[變數](completion-variables.md#宣告的位置仍然不開清單)的判斷不列。
- 不封閉（`SET IDENTITY_INSERT ` 之後是資料表）：名稱照常，只有關鍵字換掉。

片語的字不看目標：目標說的是這一格要哪一種名稱，剖析器已證明片語的字接得上。候選清單依目標分派時
（資料行、資料指標、型別）也要接上片語的字：`CREATE TABLE t (PERIOD ` 的目標是型別，`FOR` 由片語給。`EXEC ` 的目標是程序，
照目標過濾的話 `EXEC AS` 的 `AS` 永遠列不出來；`OPEN ` 的目標是資料指標，`SYMMETRIC`、`MASTER` 也是這樣並列。
提示與引數的封閉清單也一樣：`USE d⏎ALTER TABLE t REBUILD WITH (` 的前一格判不出位置，片語只比對到可能，
`WITH (` 同時列資料表提示與 `ONLINE`。
附加片語除外：它只補目錄給不了的字、比對永遠是可能，與目錄的關鍵字一樣照目標過濾——`FETCH NEXT FROM GLOBAL `
之後是資料指標名稱，不列 `GENERATED`。

「可能」出現得越少，清單越準；它的來源是位置分析的 `Any`，該補的是分析器。

## 刻意沒收的

- `WITHIN GROUP (…) ` 之後還列 `WITHIN`：照前面那次呼叫算才接得到 `OVER`，分開要多一個位置。
