# 關鍵字目錄與位置分層

本頁只處理 T-SQL 關鍵字的產生與位置旗標；哪些位置不列資料庫物件見[名稱位置](completion-name-slots.md)，
子句回溯見[子句邊界](completion-boundaries.md)，SET 選項這類非保留字見[子句片語](completion-phrases.md)。

## 產生與維護

T-SQL 關鍵字由 `tools/Generate-Keywords.ps1` 反射 SSMS 自帶的 ScriptDom 產生，
commit 進 `Core/Keywords/SqlKeywordCatalog.Generated.cs`，換 SSMS 版本重跑。產生器在 `tools/SqlAssist.KeywordGenerator`，
結果快取在 `artifacts/cache/`：只存剖析器的回答（拒收落在
哪一段、整段完不完整），解讀每次重算；ScriptDom 版本、拒收錯誤碼或 `ProbeFacts.cs` 的 `<cache-facts>`
區段一變就整份作廢，懷疑不一致時加 `-NoCache` 重建。每個階段都自我驗證，不猜字：

1. **取字面值**：`TSqlTokenType` 的成員名稱大寫後丟回 tokenizer，對得回原成員才採用；
   camelCase 的再試補底線的寫法（`CURRENT_TIMESTAMP`、`IDENTITY_INSERT`）。

2. **定位置**：把每個關鍵字塞進樣板的洞裡剖析，依錯誤碼判定它在該位置合不合法。
   46001、46005、46010、46014、型別限制（46004、46051：報完就停，續尾都沒驗）與「不是這裡的選項」那一族（從剖析器的訊息資源撈）= 不合法；
   46029（未預期的檔案結尾）= 合法，只是語句沒寫完。單一續尾會誤判，所以每個位置試一組續尾取聯集。非保留字要以**關鍵字身分**過才算
   屬於那個位置：同一組續尾換成普通名稱也過的話，那一次只證明它能當名字。
   MERGE 少了分號只報 46097、之後不再檢查：補上分號重剖析，整段比對仍有 46097 就算拒收。

3. **寫完一項或一句的字**：樣板接上它就是完整的一句。以它結尾的是片段（`NULL`、`DESC`）時
   之後往回找子句；是語句本身（`BREAK`）時是[語句界線](completion-boundaries.md#語句的界線)，
   那一句再也接不了別的字時之後才是語句開頭（`COMMIT` 還接 `TRAN`，照舊 `Any`）。

手寫的只有每個位置的樣板，分類全部由剖析器決定。樣板必須是分析器判得出、
而且回報含該位置的文字：樣板表隨產物輸出成 `SqlKeywordCatalogData.Templates`，
由 Core 測試逐條回驗。只有產生器分得出的位置是自欺。

非保留字是唯一的例外：`THROW`、`APPLY`、`NOLOCK` 不在 token 列舉裡，由產生器
`Data/KeywordSupplements.cs` 手寫，位置一樣自動分類。

### 召回稽核

`SqlKeywordRecallTests` 以[召回稽核](completion-audit.md)的判定守 `RecallCorpus.sql` 零漏。
沒有豁免名單：刻意不收的不進語料，理由寫在[那一格的文件](completion-phrases.md#刻意沒收的)。

### 依位置分層

每個關鍵字帶著「可以出現在哪些位置」，由 `SqlKeywordPositionAnalyzer` 判斷位置後過濾：

```text
（語句開頭）          → SELECT、USE、BACKUP、RESTORE、CREATE…
SELECT * FROM t ORDER BY a  → ASC、DESC
SELECT * FROM t GROUP BY a  → HAVING、ORDER（GroupByTail）
DECLARE c CURSOR LOCAL → FOR（CursorOption；選項由片語給）
CREATE SEQUENCE s AS int → START、NO（SequenceOption；選項由片語給）
ALTER TABLE t         → ADD、ALTER、DROP、CHECK、NOCHECK、SET、WITH、MERGE
ALTER TABLE t ADD     → CONSTRAINT、DEFAULT、PRIMARY、FOREIGN、UNIQUE、CHECK、INDEX…
CREATE TABLE t (      → CONSTRAINT、PRIMARY、UNIQUE、INDEX…，沒有 DEFAULT（ColumnDefinition）
CREATE TRIGGER tr ON t AFTER → INSERT、UPDATE、DELETE（TriggerEvent）
OVER (ORDER BY a      → ASC、DESC、ROWS、RANGE（WindowOrderTail）
FOR XML RAW,          → TYPE、ROOT、ELEMENTS（OptionItem）
```

位置切在「游標前一個詞元」之後，那是分析器認得的粒度：它分不出
`FROM t ` 的 `t` 是資料表還是聯結對象，目錄也不假裝分得出來。

#### 判不出位置的字只在判不出位置時出現

產生器判不出位置的深層子句字（`STOPLIST`、`NOLOCK`…）產出為 `None`，`None` 只有
這一個意思：分析器也判不出位置（`Any`）時才出現。規則在 `SqlKeywordPositionExtensions.Allows`，
關鍵字、內建函式與片段共用。放行到每個位置的話 `SELECT S` 列得出 `STOPLIST`；
整個藏起來也不行，判不出位置的地方（`WITH (`、`= ANY`）正是它們的用處。
某個字的用法落在判得出的位置時，該補的是產生器的樣板，不是放寬這一條。
「這一格是新名字」是另一軸（`SqlCompletionSlot`），不借用 `None`。

#### `Any` 是給「判不出來」用的，不是給「不想判」用的

`Any` 含所有位元，而過濾是 `positions & 目前位置`，所以**回一次 `Any` 等於
所有關鍵字與片段進場**：同一個前綴，`ORDER BY C` 回 `Any` 時有 118 個候選、欄位掉到第 14；
回 `OrderByColumn` 只剩 30 個，`cs` 之後就是欄位。

因此 `OrderByColumn`（`ORDER BY`／`GROUP BY` 的欄位，含逗號之後）與
`AlterTableAction`／`AlterTableAdd`／`AlterTableColumn`、`BlockEnd`、`IfBodyEnd`、`CursorOption` 這類敘述自己的格子
都是**自己的成員**，不借用 `Any`。欄位之後是 `OrderByTail`（`ASC`／`DESC`）與 `GroupByTail`（`HAVING`）。
`SELECT a INTO t `、`OFFSET 10 `、`REFERENCES u (a) ` 這類子句尾端也各有位置，不借長得像的 `OrderByTail`。
資料行的型別或計算運算式之後是 `ColumnDefinitionTail`，CREATE TABLE、`ALTER TABLE t ADD`／`ALTER COLUMN` 與 OPENJSON 的 `WITH (` 共用；
之後每寫完一個運算元（`NULL`、右括號）仍是它，寫到一半的（`NOT`、`MASKED`）交給片語，停在 `DEFAULT` 上的照舊 `Any`。
`FunctionCallTail` 是疊加位元：函式呼叫之後多接 `OVER`，`WITHIN GROUP (…)` 之後也是；限定名稱（`dbo.fn_Fee(a)`）是 UDF，不加。

`ALTER TABLE` 那三個位置認的是「往回正好是 `ALTER TABLE` 加一個含點號的名稱單位」，
`ADD` 清單的逗號之後走回同一個 `ADD`（`SqlTokenNavigator.SkipQualifiedNameBackward`）。
