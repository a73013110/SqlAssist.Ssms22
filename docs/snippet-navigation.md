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
| `FROM `、`ALTER TABLE `、`DROP TABLE IF EXISTS `… | 列出資料表與檢視 |
| `CREATE INDEX ix ON ` 這種 DDL 的 `ON` | 同上；與 JOIN 條件的 `ON` 由 `SqlDdlTarget` 分開 |
| `別名.` 這種限定字 | 列出那一張表的欄位 |
| `CREATE TABLE `、`CREATE VIEW `、`CREATE PROCEDURE `… | 不參與；那是使用者正要取的新名字 |
| `CREATE INDEX … ON t (` 這種推不出目標又沒有限定字的位置 | 打了字才有；列出敘述看得到的欄位 |

第四列不必特別處理：那些位置推不出目標，前綴又是空的，分析器自己就回報不參與。
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
  那是唯一的參與條件：`cix` 的 `ON $table$ ($column$)` 那一格推不出目標，
  一律截到起點的話前綴永遠是空的，那一格就永遠不會有清單。打了字之後由敘述範圍
  把 `ON` 後面那張表的欄位交出來。這與 `SELECT |` 要打了字才有清單是同一條規則。
- 引擎**沒有** `SetFieldValue`，只有改預設值的 `SetFieldDefault`。換掉格子內容靠
  的就是一般的緩衝區編輯，範圍即整格；引擎的欄位標記會自己跟上。

三個判斷都比對**當下**的文字而不是記一個旗標：使用者一打字，格子內容就不再等於
預設值，三處自然同時恢復正常，不必有人去清狀態。

`GetFieldSpan` 對同名欄位只回第一個實例的範圍，所以游標停在第二個實例時這一格
沒有清單。內建片段裡**只有純粹的重複名稱**共用一格（`cur` 的游標名稱、`cte` 的
CTE 名稱），那些位置本來就要同步；要列清單的格子一律各自一個名稱。
這條限制以前是靠 `mg` 學到的：比對鍵曾經 target 與 source 共用一個名稱，
結果選了目標的鍵、來源那一邊就跟著變成同一個名字，而兩張表不一定同名。

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
