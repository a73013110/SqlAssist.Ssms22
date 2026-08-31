# 程式碼片段

內建 40 筆 SQL Server 2016 SP1 以上可用的片段；由
`工具 → SqlAssist → 程式碼片段…` 增刪修，也可以從設定頁進入。

| 分類 | 捷徑 |
|---|---|
| SELECT | `ssf`、`st100`、`st1`、`ssc`、`sd` |
| DML | `ii`、`iis`、`ui`、`df`、`mg` |
| DDL | `cdb`、`ctb`、`cv`、`cp`、`cf`、`citvf`、`cix`、`at`、`dt`、`ap`、`af` |
| 流程控制／交易 | `beg`、`bt`、`ct`、`rt`、`ife`、`ifne`、`wl`、`tc`、`cs`、`cur`、`trn` |
| 查詢子句／其他 | `ij`、`lj`、`ob`、`gb`、`cte`、`sno`、`ptt`、`tt` |

`cf` 是純量函式、`citvf` 是內嵌資料表值函式。CASE 使用 `cs`，不占用 T-SQL
關鍵字 `CASE`。`ui`、`df` 與 `mg` 預設含 `1 = 0`，仍必須在執行前檢查條件；
這些危險片段在沒有輸入任何前綴時不主動顯示，輸入捷徑或按下 Snippet 分類仍找得到。

`ssf`、`ap`、`af` **刻意不是 Tab Stop**，而是 `caret` 加接續建議。它們要填的是
資料表、預存程序與函式的**真實名稱**，那份清單來自連線的中繼資料；換成
`[dbo].[TableName]` 這種靜態欄位等於把這個擴充最核心的東西換掉。`ap` 更是整條
鏈的起點——選到程序之後由 `SqlCommitExpander` 放進可執行的完整定義
（見 [completion.md](completion.md)）。要 CREATE 的骨架請用 `cp`、`cf`、`citvf`，
那三筆才是 Tab Stop。

## 佔位符與 Tab 導航

- `$名稱$` 是欄位。集合與 Tab 順序一律由程式碼中的**首次出現順序**推導；
  `placeholders` 只保存預設值與說明，載入時由 `Reconcile()` 自動自癒。
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

`triggerFollowUp` 只對 `caret` 有效，`tabStops` 會強制關掉：原生 session 開著時
再開建議清單，placeholder 的預設值會被當成篩選前綴。

按鍵優先順序只有一份，寫在 `Ssms22/Editor/SqlTabCommandHandler`：

1. Completion 清單開著時，Tab／Enter 先提交清單。
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
不要把 40 筆內容寫進 C#，也不要放進 VSIX 安裝步驟複製到使用者目錄。

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
      "id": "builtin.st100",
      "category": "select",
      "shortcut": "st100",
      "title": "SELECT TOP (100)",
      "description": "查詢資料表前 100 筆",
      "expansionMode": "tabStops",
      "positions": ["StatementStart", "BlockStart"],
      "code": "SELECT TOP (100) *\nFROM [$schema$].[$table$]$end$;",
      "placeholders": [
        { "id": "schema", "default": "dbo", "tooltip": "結構描述" },
        { "id": "table", "default": "TableName", "tooltip": "資料表名稱" }
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
