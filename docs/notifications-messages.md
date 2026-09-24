# 通知訊息

文案契約與逐項訊息，`Core/Notifications/NotificationCatalog` 是唯一出處。
三軸與可見度見[可見度](notifications-visibility.md)，當初的取捨見[設計](notifications-design.md)。

## 文案契約

- `Title` 是動詞開頭的現在進行式短語，**常數字串**，不含物件名稱、狀態與標點，
  不超過 14 個中文字。熱路徑不得組字串：通知被隱藏時那一份也已經配置完畢。
- `Subject` 放物件限定名稱，`Document` 放發起的文件，`Source` 放資料庫，三者不混進 `Title`。
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

## Metadata

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 載入物件清單 | | Ambient／Typing | Info |
| 載入欄位與定義 | 限定名稱 | 依呼叫端 | Info |
| 載入索引與條件約束 | 限定名稱 | 依呼叫端 | Info |
| 載入系統物件 | | Typing | Info |
| 載入定序名單 | | Typing | Info |
| 載入資料庫定序 | | Typing | Info |
| 載入資料庫清單 | | Typing | Info |
| 載入連結伺服器 | | Typing | Info |
| 連線到其他資料庫 | 目標資料庫 | 依呼叫端 | Info |
| 透過連結伺服器查詢 | 連結伺服器 | 依呼叫端 | Notice |
| 命中快取 | 限定名稱 | 依呼叫端 | Trace |

清單類的查詢沒有單一主體：資料庫已經在 `Source` 上。跨資料庫與連結伺服器另外有標題：
兩者都慢，而 `Source` 只有資料庫名，看不出這一次是遠端跳躍；連結伺服器用 `Notice`
跨過種類開關。命中快取只進統計，永不上畫面。

三者都由 `SqlMetadataCatalog.TryLoad` 接線。遠端跳躍走 `NotificationCenter.BeginDetached`，
只由最外層那一次開：巢狀查詢已經併進外層，跟著開會讓同一次載入冒出好幾列一樣的提示。
查詢失敗時送進 `SqlMetadataFailure.Reporter` 的那一行仍然帶著物件名稱：紀錄檔少了它就分不出
是哪一個物件，而組字串只在真的失敗時付一次。

## Completion

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 準備建議清單 | 目標種類（資料行、資料來源…） | Typing | Info |
| 排序建議清單 | | Typing | Debug |
| 篩選建議清單 | | Typing | Debug |
| 載入建議說明 | 限定名稱 | Typing | Debug |
| 解析限定名稱 | 前置字 | Typing | Debug |
| 顯示函式參數提示 | | Typing | Debug |

目標種類由 `switch` 回常數短語，不用 `ToString()`：那一段每按一次鍵都走一次。

## Preview

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 準備物件提示 | 解析後以 `Report` 回報限定名稱 | Typing | Info |
| 載入結構預覽 | 限定名稱 | Typing | Info |
| 更新預覽選取 | | Typing | Debug |
| 產生物件指令碼 | 限定名稱 | User | Info |
| 執行結構健檢 | 限定名稱 | User | Debug |

物件提示的通知要蓋住解析那一段，而物件要等解析完才知道是誰，限定名稱因此走 `Report`。

## Analysis、Editing 與 Navigation

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 分析 T-SQL 區塊 | | Typing | Debug |
| 展開 SELECT ＊ | | User | Info |
| 展開語句樣板 | 限定名稱 | User | Info |
| 展開程式碼片段 | 片段捷徑 | User | Debug |
| 移至定義 | | User | Info |
| 開啟未連線的定義視窗 | 限定名稱或作業步驟 | User | Info |
| 產生定義指令碼 | 限定名稱 | User | Info |
| 開啟新查詢視窗 | 限定名稱 | User | Debug |

文件名稱一律在 `Document` 上，不重複放進 `Subject`。移至定義在開始時還不知道游標下
是哪個物件，物件名稱由後面兩則帶上。SQL Search 的結果不在查詢視窗那一台時換成「開啟未連線的
定義視窗」，`Source` 放來源伺服器：只寫「移至定義」的話，使用者要到按 F5 跳出連線對話框才知道
那個視窗沒有連線。

## Results、Snippets、Settings 與 Package

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 初始化 SqlAssist | | Startup | Info |
| 建立中繼資料連線 | | Ambient | Info |
| 重新確認連線 | | Ambient | Debug |
| 重新載入設定 | | Ambient | Debug |
| 重建主題筆刷 | | Ambient | Trace |

建立連線寫的是 `Source` 而不是主體：資料庫是「資料從哪裡來」，放 `Subject` 會讓同一個字
在中繼資料那幾列出現在兩種位置上。伺服器名稱只有連線字串取得到，那一份不進通知。設定重新
載入分不出是設定頁按下去還是殼層自己推的，因此一律 `Ambient`。主題筆刷的初始化不追蹤——
它是「初始化 SqlAssist」裡面的一步。

## SQL Memory

| 標題 | 形式 | 來源 | 等級 |
|---|---|---|---|
| 啟用／停用 SQL Memory | 範圍 | Ambient | Info |
| 測試 SQL Memory 儲存 | 範圍 | User | Info |
| 整理 SQL Memory | 範圍 | User | Info |
| 維護 SQL Memory | 範圍／事件 | User／Ambient | Info／Debug |
| 清除 SQL Memory 紀錄 | 範圍 | User | Info |
| 備份 SQL Memory | 範圍 | User | Info |
| 重建 SQL Memory 資料庫 | 範圍 | User | Info |
| 回溯收藏版本 | 範圍 | User | Info |
| 丟棄 SQL 擷取 | 事件 | Ambient | Notice |

逐處見[用量](sql-memory-usage.md)與 [UI](sql-memory-ui.md)；背景維護批次失敗沿用「維護」標題。
當場完成的動作留在視窗裡，只有會等儲存的長操作才開通知。

丟棄走 `NotificationCenter.Post`；標題動詞開頭，才不會轉成「已未保存…」。它是**失敗**——那一段
SQL 完全沒有記錄，值得進「通知失敗」回看，而使用者沒有可做的決定。原因短語由 Core 給
（`SqlCaptureDroppedEventArgs.Reason` 是常數，擷取在熱路徑上）。

容量警戒與首次擷取要使用者決定，是下面的提醒。首次擷取在第一次真正接上儲存時說一次，記在
狀態存放區裡，每台電腦只出現一次：總開關預設是開的，不能安靜地開始記錄使用者的 SQL。那一句
只說資料在這台電腦、去哪裡看、去哪裡關，敘述在 `NotificationCatalog.SqlMemoryFirstCaptureNotice`。

## 更新

手動的「檢查更新」是範圍（`Update`／`User`／`Info`，要等 HTTP）：「已是最新版」與「查不到」留在
那一列，查不到記為失敗——使用者按了之後要知道這一次沒有答案。有新版時那一列只收尾，結論是下面的
`update` 提醒（`User`，不理會略過與稍後）。啟動時的自動檢查不開活動，只有新版而且不是略過的那一版
才送提醒（`Ambient`／`Notice`）：每次開 SSMS 都說一次「已是最新版」是噪音。版本號與文案由
`SqlAssistUpdateCheck` 給。

## 提醒

標題不守動詞開頭的契約：不轉過去式，回答「要決定什麼」。叉號 ToolTip 是「稍後提醒」；
按鈕由次要排到主要，括號是參數。

| 鍵 | 標題 | 按鈕 | 嚴重度 |
|---|---|---|---|
| `update` | SqlAssist 有新版 | 略過此版本 `update.skip`（版本號）、前往下載 `update.download`（那一版的發行頁） | Info |
| `sqlmemory.capacity` | SQL Memory 超過容量警戒 | 開啟維護 `sqlmemory.open-maintenance` | Warning |
| `sqlmemory.first-capture` | SQL Memory 已開始擷取 | 開啟 SQL Memory `sqlmemory.open` | Info |

通知島的膠囊一項時是「標題 · 主體」，多項時是「N 項工作 · 成功數/總數」，由 `CapsuleSummary` 產生；
多項的那一句就是清單抬頭（`ProgressSummary`），變形時只有數字會變。抬頭的失敗標記是「N 項失敗」，
附條的動作是「查看」與「回到提醒」，暫看時的摘要是「N 則提醒待處理」。

## 已知缺口

等待 `SqlMetadataCatalog` 的 `_snapshotGate` 期間沒有排隊狀態，畫面上也看不出在等；
結果格線、片段存放與掃描指令碼宣告三處尚未接線；`Trace` 的快取命中只進紀錄與記憶體歷史。
