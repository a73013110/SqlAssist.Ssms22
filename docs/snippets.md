# 程式碼片段

內建 43 筆 SQL Server 2016 SP1 以上可用的片段；由
`工具 → SqlAssist → 程式碼片段…` 增刪修，也可以從設定頁進入。

| 分類 | 捷徑 |
|---|---|
| SELECT | `ssf`、`st100`、`st1`、`ssc`、`sd` |
| DML | `ii`、`ui`、`df`、`mg` |
| DDL | `cdb`、`ctb`、`cv`、`cp`、`cf`、`citvf`、`cix`、`at`、`dt`、`ap`、`af` |
| 流程控制／交易 | `be`、`bt`、`ct`、`rt`、`ife`、`ifne`、`wl`、`tc`、`cs`、`cur`、`trn` |
| 查詢子句／其他 | `ij`、`lj`、`rj`、`fj`、`cj`、`ca`、`oa`、`ob`、`gb`、`cte`、`sno`、`ptt` |

`cf` 是純量函式、`citvf` 是內嵌資料表值函式。CASE 使用 `cs`、BEGIN…END 使用
`be`，都不占用同名的 T-SQL 關鍵字。`ui`、`df`、`mg` 與 `dt` 標成危險片段：
沒有輸入任何前綴時不主動顯示，輸入捷徑或按下 Snippet 分類仍找得到。

## 半句話加接續建議

17 筆片段**刻意不是 Tab Stop**，而是 `caret` 加接續建議：插入單獨一行的半句話，
游標停在尾巴，接著由建議清單接手。

| 片段 | 插入 | 接著列出 |
|---|---|---|
| `ssf`、`st100`、`st1`、`ssc`、`sd` | `SELECT * FROM `、`SELECT TOP (100) * FROM `… | 資料表與檢視 |
| `ii` | `INSERT INTO ` | 資料表與檢視；提交時展開欄位清單與 `VALUES` |
| `ui`、`df` | `UPDATE `、`DELETE FROM ` | 資料表與檢視 |
| `ij`、`lj`、`rj`、`fj`、`cj` | `INNER JOIN `… | 資料表與檢視 |
| `ca`、`oa` | `CROSS APPLY `、`OUTER APPLY ` | 函式 |
| `ap`、`af` | `ALTER PROCEDURE `、`ALTER FUNCTION ` | 程序、函式；`ap` 提交時放進完整定義 |

它們要填的是資料表、程序與函式的**真實名稱**，那份清單來自連線的中繼資料；
換成 `[dbo].[TableName]` 這種靜態欄位等於把這個擴充最核心的東西換掉。`ii` 與
`ap` 更是整條鏈的起點——選到資料表或程序之後由 `SqlCommitExpander` 放進可執行的
整句（見 [completion.md](completion.md)）。要 CREATE 的骨架請用 `cp`、`cf`、
`citvf`，那三筆才是 Tab Stop。

**接得下去的條件是「展開出來的那一行結尾剛好是一個會列出東西的關鍵字」**，
因為接續清單的內容由 `SqlCompletionContextAnalyzer` 從游標前一個詞元推出來。
尾巴多一個分號、括號或換行都會讓下一步變成一般清單，而症狀只是「清單沒有跳
出來」，沒有任何錯誤。守門的是
`SqlSnippetDefaultsTests.接續片段展開後落在會列出該類物件的位置`，它連
`CompletionIntent` 一起比——`ii` 落在 `DataSource` 還不夠，要 `InsertStatement`
才會展開成欄位清單，退化成 `Reference` 的話只是把資料表名稱補上去。

單行也是刻意的。`ij`、`lj` 曾經連 `AS t` 與 `ON 1 = 1` 一起插進去，代價是每次
都要回頭刪掉猜錯的別名與條件。改成單行之後別名與 `ON` 要自己打——`ON` 有關鍵字
自動大寫接著，而別名那一格本來就不開清單（見 [completion.md](completion.md) 的
「沒有 AS 的別名靠換行分辨」）。

`sd` 展開的是 `SELECT DISTINCT * FROM `。`DISTINCT *` 通常不是最終要的，但選完
資料表之後把游標移到 `*` 按 Tab 就展開成完整欄位清單再挑，比 `[$column$]` 一次
只填得了一個欄位好用。

`ca`、`oa` 之後的清單會連純量函式一起列——中繼資料把三種函式對應到同一個
`SuggestionKind`，要分開得新增一種類別。多幾個選不中的名稱是多按幾下，而把整個
`CompletionTarget.Function` 讓掉的話那個位置就完全沒有補字。

Tab Stop 樣板一律**不寫方括號**。要不要加括號由「插入物件時加上方括號」
（`sqlAssist.suggestions.useSquareBrackets`，預設關閉）決定，樣板自己寫死
`[dbo].[TableName]` 的話，同一份指令碼裡就會出現兩種風格，而那個差別使用者從來
沒有要求過。含空白或保留字的名稱仍然要自己補上括號。

## 佔位符與 Tab 導航

- `$名稱$` 是欄位。集合與 Tab 順序一律由程式碼中的**首次出現順序**推導；
  `placeholders` 只保存預設值與說明，載入時由 `Reconcile()` 自動自癒。
- 結構描述與物件名稱**合成一格**，預設值是 `dbo.TableName` 這種完整名稱，
  不寫成 `$schema$.$table$`。拆成兩格的代價是每個物件都要按兩次 Tab，而第一格的
  答案幾乎永遠是 `dbo`；更關鍵的是建議清單依設定插進來的三種寫法
  （`dbo.Lib_Reader`、`Lib_Reader`、`[dbo].[Lib_Reader]`）只有合成一格才填得下，
  拆開時第三種根本放不進去。守門的是
  `SqlSnippetDefaultsTests.物件欄位不拆成結構描述與名稱兩格`。
- 同名欄位會同步修改。
- `$end$` 是最後落點，內建片段至多一個。
- `$selected$` 保留給原生 Expansion Engine；從建議清單展開時通常是空字串。
- 沒有宣告的 `$名稱$` 與不成標記的 `$` 原樣保留。轉成原生 XML 時，
  `SqlSnippetExpansion` 會把字面 `$` 轉成 `$$`，不讓引擎誤認成欄位。

`expansionMode` 有兩種：

| 值 | 行為 |
|---|---|
| `tabStops` | 使用 SSMS 原生 Expansion Engine；Tab 下一欄、Shift+Tab 上一欄、最後一次 Tab 到 `$end$` |
| `caret` | 一次插入完整文字，只把游標移到 `$end$`；可搭配接續建議 |

`triggerFollowUp` 只對 `caret` 有效，`tabStops` 會強制關掉——但那不再代表
Tab Stop 沒有清單。

## Tab Stop 欄位的建議清單

進入任何一格都會把建議清單重開一次（`SqlSnippetExpansionController` 在插入完成、
`MoveNext()` 與 `MovePrevious()` 成功之後各排一次，走的仍是 `SqlCompletionReopen`
的三步驟）。**刻意不加「這一格要列什麼」的宣告欄位**：那份判斷已經有一份，在
`SqlCompletionContextAnalyzer`，它讀的是使用者實際編輯過的文字。多一份宣告的症狀是
樣板把 `FROM` 改成別的字、宣告卻沒跟著改，而清單靜靜地不再出現。

因此哪幾格有清單完全由樣板的文字決定：

| 這一格前面是 | 結果 |
|---|---|
| `MERGE INTO `、`USING `、`FROM `、`ALTER TABLE `… | 列出資料表與檢視 |
| `target.`、`source.` 這種別名限定字 | 列出那一張表的欄位 |
| `CREATE TABLE `、`CREATE VIEW `、`CREATE PROCEDURE `… | 不參與；那是使用者正要取的新名字 |
| `INSERT (` 這種推不出目標又沒有限定字的位置 | 打了字才有；列出敘述看得到的欄位 |

第二列不必特別處理：那些位置推不出目標，前綴又是空的，分析器自己就回報不參與。
兩列各有守門測試（`物件欄位落在會列出資料來源的位置`、`新建物件的名稱欄位不主動開清單`）。

過去 `tabStops` 不敢開清單的理由是「placeholder 的預設值會被當成篩選前綴」，
那是真的——`dbo.TargetTable` 當前綴時清單一定是空的。解法是**適用範圍改成整格，
而那一格裡的預設值當它不存在**：

- 範圍向引擎要（`IVsExpansionSession.GetFieldSpan`），不從 Selection 或游標方向推。
  進入欄位時游標可能停在頭也可能停在尾，而使用者拖選之後 Selection 就完全不是
  欄位邊界了；只有引擎手上那份標記會跟著每一次編輯移動。
- **整格還是樣板填的預設值**時，上下文分析截到這一格的起點（`ResolveAnalysisEnd`），
  排名器也把它視為空前綴（`GetTypedText`）。少了前者，限定字是 `dbo`，插進去的
  名稱就少了結構描述；少了後者，`dbo.TargetTable` 比不中任何一個資料表名稱，
  而篩選一個都沒中就會回 null 讓平台把剛開的 session 關掉——症狀是
  「Tab 進去沒有清單，打了字才有」。
- **使用者一打字就不再截斷**。他打的那幾個字就是前綴，而且對無限定字的格子來說
  那是唯一的參與條件：`INSERT ($targetInsert$)` 推不出目標，一律截到起點的話
  前綴永遠是空的，那一格就永遠不會有清單。打了字之後由敘述範圍把 target 與
  source 兩張表的欄位都交出來。這與 `SELECT |` 要打了字才有清單是同一條規則。
- 引擎**沒有** `SetFieldValue`，只有改預設值的 `SetFieldDefault`。換掉格子內容靠
  的就是一般的緩衝區編輯，範圍即整格；引擎的欄位標記會自己跟上。

三個判斷都比對**當下**的文字而不是記一個旗標：使用者一打字，格子內容就不再等於
預設值，三處自然同時恢復正常，不必有人去清狀態。

`GetFieldSpan` 對同名欄位只回第一個實例的範圍，所以游標停在第二個實例時這一格
沒有清單。內建片段因此**不再讓兩格共用一個名稱**：`mg` 的比對鍵、更新欄位與新增
欄位都拆成 target 與 source 各一格。同名同步原本是刻意的，但代價是選了目標的鍵，
來源那一邊就跟著變成同一個名字——而兩張表不一定同名。

按鍵優先順序只有一份，寫在 `Ssms22/Editor/SqlTabCommandHandler`：

1. Completion 清單開著時，Tab／Enter 先提交清單。提交發生在 Snippet 欄位裡而且
   按的是 Tab 時，同一次按鍵接著走到下一格——那一步排在這一輪命令之後由
   `SqlAsyncCompletionCommitManager` 自己做，不靠
   `CommitBehavior.RaiseFurtherReturnKeyAndTabKeyCommandHandlers` 把命令鏈接下去：
   那個旗標要求本處理常式與平台的先後順序固定，而兩者目前都只寫 `Before=default`。
   平台若在 Tab 提交時傳的不是 `\t`，退化成「再按一次 Tab 才跳格」。
   Enter 不跳格，它在 session 裡的語意仍然是換行並結束欄位追蹤。
2. Snippet session 開著時，Tab／Shift+Tab 導航欄位。
3. 沒有 session 時，Tab 才嘗試展開 `SELECT *`。
4. 都不符合就交回編輯器做一般縮排。

Esc 先關 Completion 或獨立預覽，再結束 Snippet session。Enter 在 session 中仍是換行：
先結束欄位追蹤，再交回編輯器。Session 開著時暫停關鍵字自動大寫，避免外部編輯破壞
原生欄位標記；在欄位內一般輸入仍會照常叫出 Completion。

提交時先讓 Completion session 關閉，再於 Dispatcher Background 呼叫
`IVsExpansion.InsertSpecificExpansion`。原生 API 不可用且緩衝區尚未改動時，自動退回
`caret` 模式；若引擎在回報失敗前已經改動文字，禁止再插一次 fallback，以免內容重複。

引擎**不會**自己縮排：`Code` 是逐字插進去的，第 2 行之後一律從第 0 欄開始。
`IVsExpansionClient.FormatSpan` 是唯一的補救點，回報 `S_OK` 卻什麼都不做等於告訴
引擎「已經排好了」。SqlAssist 在那裡把插入點所在行的前導空白補到後續每一行
（空白行不補），而且只在插入那一次做——欄位導覽時引擎可能再叫一次，
補第二遍就會多推一層縮排。

## 內建值與使用者 override

內建定義只有一份：
`src/SqlAssist.Core/Snippets/DefaultSnippets.json`，以 Embedded Resource 隨 VSIX 發布。
不要把 43 筆內容寫進 C#，也不要放進 VSIX 安裝步驟複製到使用者目錄。

使用者檔位於 `%APPDATA%\SqlAssist\snippets.json`，v2 只存：

- 修改過的內建項目；
- `{ "id": "builtin...", "disabled": true }` 的停用紀錄；
- 使用者新增的完整項目。

檔案不存在代表「完全使用內建值」，不會在第一次啟動時建檔。這讓新版 VSIX 可以直接
更新未自訂的內建片段。管理介面會標示「已自訂」與「已停用」，並提供「還原此預設」；
全部還原後寫出的 override 清單是空的。

```json
{
  "version": 2,
  "snippets": [
    {
      "id": "builtin.ctb",
      "category": "ddl",
      "shortcut": "ctb",
      "title": "CREATE TABLE",
      "description": "建立資料表",
      "expansionMode": "tabStops",
      "positions": ["StatementStart", "BlockStart"],
      "code": "CREATE TABLE $schema$.$table$\n(\n    $column$ $dataType$ NOT NULL\n)$end$;",
      "placeholders": [
        { "id": "schema", "default": "dbo", "tooltip": "結構描述" },
        { "id": "table", "default": "TableName", "tooltip": "資料表名稱" },
        { "id": "column", "default": "ColumnName", "tooltip": "欄位名稱" },
        { "id": "dataType", "default": "INT", "tooltip": "資料型別" }
      ]
    },
    { "id": "builtin.dt", "disabled": true }
  ]
}
```

`category` 是固定集合：`select`、`dml`、`ddl`、`controlFlow`、`clause`、`other`；
不認得的值落到 `other`。`positions` 重用 `SqlKeywordPosition`，缺席為 `Any`。

**`positions` 給得太緊的症狀是全靜默的**：使用者只覺得「這個片段有時候有、
有時候沒有」。語句級片段一律要同時給 `StatementStart` 與 `BlockStart`——分析器在
`BEGIN` 之後只回報 `BlockStart`，只給前者的話整批片段在 `BEGIN…END` 區塊裡會消失。
守門的是 `SqlSnippetDefaultsTests.內建片段在它自然的位置找得到`；新增片段時
要在那份表格加一行。

`minimumSqlServerVersion` 不存在：產品下限已固定，為它查詢每條連線的版本只會把資料庫 I/O
帶進按鍵路徑。

## 遷移、相容與存檔

- v1 是完整清單。第一次讀到時，先把原檔備份成
  `%APPDATA%\SqlAssist\snippets.v1.backup.json`（只寫一次），再與不可修改的 v1
  三筆凍結快照比較，轉成最小 v2 override。
- v1 自訂捷徑若在 v2 成為內建捷徑，會轉成該內建 ID 的 override，不產生兩筆撞名項目。
- `version > 2` 時可以讀已知欄位，但整份進入唯讀模式，避免舊版把新欄位覆蓋掉。
- v2 保留頂層 `snippets` 鍵並只新增欄位；降回舊版時，舊讀取器至少仍看得到完整 override
  與自訂項目。
- 存檔先寫同目錄暫存檔，再用 `File.Replace` 原子置換；目標不存在時才用 `File.Move`。
- 允許 JSON 註解與尾隨逗號。整份語法壞掉時保留原檔、切成唯讀、顯示錯誤，並繼續提供內建片段。

不使用檔案監看器：清單只在第一次使用時載入並維持穩定參考，管理介面成功存檔才換快照。
因此按鍵路徑沒有磁碟 I/O，也不會因每次 `Current` 產生新物件而重建整批建議。直接用文字
編輯器修改 JSON 後，需要重新啟動 SSMS 才會載入。

這是 SqlAssist 自己的格式，與 SSMS「程式碼片段管理員」的 `.snippet` 檔不互相註冊；
只有提交時把選到的項目轉成記憶體中的原生 XML。
