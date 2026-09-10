# 通知的三軸分類與可見度

`Core/Notifications/NotificationVisibility` 只篩選通知，不停用工作，也不影響工作階段
統計與診斷紀錄。顯示延遲以篩選後的工作計算，抬頭計數再以合併後的列數計算，兩處都不把
隱藏工作混入分母；合併規則見[通知提示](notifications.md)。
三軸都由呼叫端明寫，不比對顯示名稱；合併的巢狀查詢沿用父工作的三軸。

- `NotificationKind` 是子系統：Metadata、Completion、Analysis、Preview、Editing、
  Navigation、Results、Snippets、Settings、Package、Unclassified，各有一格開關。
- `NotificationOrigin` 是觸發來源：User、Typing、Ambient、Startup。
- `NotificationLevel` 是詳細度：Trace、Debug、Info、Notice。

`Origin` 為 `User` 的一律顯示，因此只有真的由使用者按鍵或命令觸發的才標它；輸入時反覆
觸發的標 `Typing`，預載、快取重整與重新確認標 `Ambient`，套件與工作階段啟動標 `Startup`。

已接線的種類與來源如下，同一列的來源是接線點寫死的值，中繼資料第二、四層由呼叫端傳入：

| 種類 | 接在哪 | 來源 | 等級 |
|---|---|---|---|
| Metadata | `SqlMetadataCatalog.TryLoad` 的七條查詢、跨資料庫與連結伺服器跳躍、快取命中 | 前景查詢與清單類 `Typing`、預載 `Ambient`、第二與第四層依呼叫端 | `Info`；連結伺服器 `Notice`、快取命中 `Trace` |
| Completion | 準備、排序、篩選建議清單，載入說明，解析限定名稱，函式參數提示 | `Typing` | 準備清單 `Info`，其餘 `Debug` |
| Analysis | 背景分析 T-SQL 區塊 | `Typing` | `Debug` |
| Preview | 準備物件提示、載入結構預覽、更新預覽選取、產生物件指令碼、執行結構健檢 | 提示與預覽 `Typing`，指令碼與健檢 `User` | 提示與預覽 `Info`，選取與健檢 `Debug` |
| Editing | 展開語句樣板、展開 `SELECT ＊` | `User` | `Info` |
| Navigation | 移至定義、產生定義指令碼、開啟新查詢視窗 | `User` | 前兩者 `Info`，開視窗 `Debug` |
| Snippets | 展開程式碼片段 | `User` | `Debug` |
| Settings | 重新載入設定、重建主題筆刷 | `Ambient` | 設定 `Debug`、主題 `Trace` |
| Package | 初始化 SqlAssist、建立中繼資料連線、重新確認連線 | 初始化 `Startup`，其餘 `Ambient` | 初始化與建立連線 `Info`、重新確認 `Debug` |
| Results | 尚未接線 | — | — |

依序判斷：總開關 → `Failed` 看「顯示所有種類的失敗」→ `Degraded` 看「顯示部分成功」→
該種類的開關關著就隱藏 → `Trace` 在詳細度不是「全部」時隱藏 → `Origin` 為 `User` 顯示
→ `Notice` 顯示 → 其餘看詳細度門檻。

預設關著的是 Completion、Analysis、Preview、Package 這四個高頻種類，其餘七個預設開，
`Unclassified` 也在其中，漏分類的新工作不會靜默消失。種類開關排在觸發來源**之前**：
關掉「程式碼片段」就是連自己按下去的展開提示也不想看，排在後面的話那幾格永遠按不動。
來源與詳細度只決定跨不跨得過降噪門檻，不覆寫種類開關。

種類開關集中在 `NotificationKindToggle.All` 的 `(Kind, moniker, 預設值, 標題)`；設定存
`NotificationKindSwitches` 遮罩、`SqlAssistMonikers.All` 併入表上的 moniker，
新增一類只動註冊檔與這張表。

詳細度下拉四階對上 `NotificationLevel`：安靜 `Notice`、一般 `Info`、詳細 `Debug`、
全部 `Trace`。`Trace` 量大到會洗掉畫面，只有明選「全部」才放行，使用者剛觸發的也一樣。

## 降級、等級與診斷

`Degraded` 對應 `Severity.Warning`：子工作失敗只把父工作標成降級，只有父工作自己的例外
才是失敗——中繼資料查詢對 `DbException` 一律降級，否則一次可正常降級的欄位查詢會讓整個
「按 Tab 展開」顯示為失敗。結果只往上升級：失敗、降級、取消、成功。降級不進「通知失敗」
列表，但沿用較長的保留期限。

詳細診斷開啟時，每個獨立工作完成寫一筆 `id/kind/severity/status/elapsedMs`；不寫來源、
SQL 或訊息。紀錄進有界佇列後由背景批次寫檔，不在完成工作的那條執行緒上開檔；佇列滿了
只記下略過幾筆，讀取紀錄與套件卸載前都會先倒完。通知隱藏不影響詳細紀錄與最近失敗；
既有平台例外與中繼資料降級的紀錄政策不變，避免重複堆疊。「通知失敗」頁顯示相同 ID、種類、等級與耗時，可與 log 對照；記憶體歷史
仍有原本上限。
