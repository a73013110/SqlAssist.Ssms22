# 片段欄位與 Tab 導航

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
- `$surround$` 是包夾錨點：沒有選取時就是一格普通欄位，有選取時改填選取的文字並退出
  Tab 導航。規則見[片段包夾](snippet-surround.md)。
- 沒有宣告的 `$名稱$` 與不成標記的 `$` 原樣保留。轉成原生 XML 時，
  `SqlSnippetExpansion` 會把字面 `$` 轉成 `$$`，不讓引擎誤認成欄位。

`expansionMode` 有兩種：

| 值 | 行為 |
|---|---|
| `tabStops` | 使用 SSMS 原生 Expansion Engine；Tab 下一欄、Shift+Tab 上一欄、最後一次 Tab 到 `$end$` |
| `caret` | 一次插入完整文字，只把游標移到 `$end$`；可搭配接續建議 |

`triggerFollowUp` 只對 `caret` 有效，`tabStops` 會強制關掉——但那不再代表
Tab Stop 沒有清單。

Tab 進格時建議清單怎麼開、範圍怎麼算，見
[Tab Stop 欄位的建議清單](snippet-field-list.md)。

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
`IVsExpansionClient.FormatSpan` 在插入那一次補後續非空行的基準縮排，欄位導覽時不重複補。
原生、游標降級與包夾預覽共用 `SqlSnippetIndentation` 的續行判定；原生逐點插入保住欄位
標記，降級一次替換並同步調整游標偏移，避免兩條路的縮排不同。
