# 通知訊息

本頁包含文案契約、各子系統措辭的理由與提醒清單；逐項標題、主體與三軸看
`Core/Notifications/NotificationCatalog` 與接線點，三軸與可見度見[可見度](notifications-visibility.md)。

## 文案契約

- `Title` 是動詞開頭的現在進行式短語，**常數字串**，不含物件名稱、狀態與標點，
  不超過 14 個中文字。熱路徑不得組字串：通知被隱藏時那一份也已經配置完畢。
- `Subject` 放物件限定名稱，`Document` 放發起的文件，`Source` 放資料庫，三者不混進 `Title`。
  `Document` 與 `Source` 分開，抬頭才能只看有文件的列：合成一格時，一列資料庫名或沒有出處的
  背景工作就會讓抬頭的檔名時有時無。文件名稱不重複放進 `Subject`。
- 完成後的措辭由目錄依 `Status` 產生，呼叫端不自己寫。
- 不放 SQL、路徑、認證或例外原文；`Message` 上限 512 字。
- 目錄的常數欄位只放標題；提醒按鈕的識別字在 `NotificationActionIds`，其餘措辭是屬性或方法。

| Status | 畫面 |
|---|---|
| Running | 標題＋主體 |
| Succeeded | 標題轉過去式；耗時超過 1 秒才顯示 |
| Degraded | 標題＋「部分資料無法取得」＋原因短語 |
| Failed | 標題＋「失敗 · 詳見關於與診斷」 |
| Canceled | 標題＋「已取消」 |

## 各子系統

- **Metadata**：清單類的查詢沒有單一主體，資料庫已經在 `Source` 上。跨資料庫與連結伺服器另有
  標題：兩者都慢，而 `Source` 看不出這一次是遠端跳躍；連結伺服器用 `Notice` 跨過種類開關。
  遠端跳躍走 `NotificationCenter.BeginDetached`，只由最外層那一次開，巢狀查詢跟著開會冒出好幾列
  一樣的提示。送進 `SqlMetadataFailure.Reporter` 的那一行仍帶物件名稱：組字串只在真的失敗時付一次。
- **Completion**：目標種類由 `switch` 回常數短語，不用 `ToString()`：那一段每按一次鍵都走一次。
- **Preview**：物件提示的通知要蓋住解析那一段，物件要等解析完才知道是誰，限定名稱因此走 `Report`。
- **Navigation**：移至定義開始時還不知道游標下是哪個物件，物件名稱由後續的定義指令碼或新查詢視窗
  帶上。SQL Search 的結果不在查詢視窗那一台時換成「開啟未連線的定義視窗」，`Source` 放來源伺服器：
  否則使用者要到按 F5 跳出連線對話框才知道那個視窗沒有連線。
- **Package 與 Settings**：建立連線寫 `Source` 而不是主體，同一個字才不會在不同列出現在兩種位置上；
  伺服器名稱只有連線字串取得到，不進通知。設定重新載入分不出是設定頁還是殼層推的，一律 `Ambient`。
  主題筆刷的初始化不追蹤，它是「初始化 SqlAssist」裡面的一步。
- **SQL Memory**：逐處見[用量](sql-memory-usage.md)與 [UI](sql-memory-ui.md)；背景維護批次失敗沿用
  「維護」標題。當場完成的動作留在視窗裡，只有會等儲存的長操作才開通知。丟棄擷取走 `Post`，標題
  動詞開頭才不會轉成「已未保存…」；它記為**失敗**——那一段 SQL 完全沒有記錄，值得進「通知失敗」
  回看。原因短語由 Core 給（`SqlCaptureDroppedEventArgs.Reason` 是常數，擷取在熱路徑上）。
  首次擷取在第一次真正接上儲存時說一次，記在狀態存放區、每台電腦一次：總開關預設是開的，不能安靜地
  開始記錄使用者的 SQL。那一句只說資料在這台電腦、去哪裡看、去哪裡關。
- **Update**：手動的「檢查更新」是範圍（要等 HTTP）：「已是最新版」與「查不到」留在那一列，查不到
  記為失敗。有新版時那一列只收尾，結論是 `update` 提醒（`User`，不理會略過與稍後）。啟動時的自動檢查
  不開活動，只有新版而且不是略過的那一版才送提醒：每次開 SSMS 都說「已是最新版」是噪音。
  版本號與文案由 `SqlAssistUpdateCheck` 給。

## 提醒

標題不守動詞開頭的契約：不轉過去式，回答「要決定什麼」。叉號 ToolTip 是「稍後提醒」；
按鈕由次要排到主要，括號是參數。

| 鍵 | 標題 | 按鈕 | 嚴重度 |
|---|---|---|---|
| `update` | SqlAssist 有新版 | 略過此版本 `update.skip`（版本號）、前往下載 `update.download`（那一版的發行頁） | Info |
| `sqlmemory.capacity` | SQL Memory 超過容量警戒 | 開啟維護 `sqlmemory.open-maintenance` | Warning |
| `sqlmemory.first-capture` | SQL Memory 已開始擷取 | 開啟 SQL Memory `sqlmemory.open` | Info |

膠囊一項時是「標題 · 主體」，多項時是「N 項工作 · 成功數/總數」，由 `CapsuleSummary` 產生；
多項的那一句就是清單抬頭（`ProgressSummary`），變形時只有數字會變。

## 已知缺口

等待 `SqlMetadataCatalog` 的 `_snapshotGate` 期間沒有排隊狀態，畫面上也看不出在等；
結果格線、片段存放與掃描指令碼宣告三處尚未接線；`Trace` 的快取命中只進紀錄與記憶體歷史。
