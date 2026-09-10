# 通知訊息

文案契約與逐項訊息，`Core/Notifications/NotificationCatalog` 是唯一出處。
三軸與可見度見[可見度](notifications-visibility.md)，當初的取捨見[設計](notifications-design.md)。

## 文案契約

- `Title` 是動詞開頭的現在進行式短語，**常數字串**，不含物件名稱、狀態與標點，
  不超過 14 個中文字。熱路徑不得組字串：現行的
  `$"{名稱} 的欄位與定義（第二層）"` 每次呼叫都配置，即使通知被隱藏也一樣。
- `Subject` 放物件限定名稱，`Context` 放來源文件或資料庫，兩者不混進 `Title`。
- 完成後的措辭由目錄依 `Status` 產生，呼叫端不自己寫。
- 不放 SQL、路徑、認證或例外原文；`Message` 上限 512 字。

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

清單類的查詢沒有單一主體：來源資料庫已經在 `Context` 上，重寫一次只是把同一個字
放兩個地方。跨資料庫與連結伺服器是新增的：兩者都慢，而 `Context` 只有資料庫名，
看不出這一次是遠端跳躍。連結伺服器用 `Notice` 跨過種類開關。命中快取只進統計，
永不上畫面。

三者都由 `SqlMetadataCatalog.TryLoad` 接線。遠端跳躍走
`NotificationCenter.BeginDetached`，只由最外層那一次開：巢狀查詢已經併進外層，
跟著開會讓同一次載入冒出好幾列一模一樣的提示。查詢失敗時送進
`SqlMetadataFailure.Reporter` 的那一行仍然帶著物件名稱——標題不含它了，而紀錄檔
分不出「哪一條查詢」加「哪一個物件」就等於沒有線索；組字串只在真的失敗時付一次。

## Completion

目前這一組完全沒有 `Begin`，是「開了建議清單卻不知道在忙什麼」的主因。

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 準備建議清單 | 目標種類（資料行、資料來源…） | Typing | Info |
| 排序建議清單 | | Typing | Debug |
| 篩選建議清單 | | Typing | Debug |
| 載入建議說明 | 限定名稱 | Typing | Debug |
| 解析限定名稱 | 前置字 | Typing | Debug |
| 顯示函式參數提示 | | Typing | Debug |

目標種類由 `switch` 回常數短語，不用 `ToString()`：那一段每按一次鍵都走一次，
而列舉名稱的字串化每次都配置一份，即使通知被篩掉也一樣。

## Preview

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 準備物件提示 | 解析後以 `Report` 回報限定名稱 | Typing | Info |
| 載入結構預覽 | 限定名稱 | Typing | Info |
| 更新預覽選取 | | Typing | Debug |
| 產生物件指令碼 | 限定名稱 | User | Info |
| 執行結構健檢 | 限定名稱 | User | Debug |

物件提示的通知要蓋住解析那一段，而物件要等解析完才知道是誰，因此限定名稱走
`Report` 而不是 `Subject`。

## Analysis、Editing 與 Navigation

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 分析 T-SQL 區塊 | | Typing | Debug |
| 展開 SELECT ＊ | | User | Info |
| 展開語句樣板 | 限定名稱 | User | Info |
| 展開程式碼片段 | 片段捷徑 | User | Debug |
| 移至定義 | | User | Info |
| 產生定義指令碼 | 限定名稱 | User | Info |
| 開啟新查詢視窗 | 限定名稱 | User | Debug |

文件名稱一律在 `Context` 上，不重複放進 `Subject`。移至定義在開始時還不知道游標下
是哪個物件，物件名稱由後面兩則帶上。「掃描指令碼宣告」尚未接線。

## Results、Snippets、Settings 與 Package

| 標題 | 主體 | 來源 | 等級 |
|---|---|---|---|
| 初始化 SqlAssist | | Startup | Info |
| 建立中繼資料連線 | 目前資料庫 | Ambient | Info |
| 重新確認連線 | | Ambient | Debug |
| 重新載入設定 | | Ambient | Debug |
| 重建主題筆刷 | | Ambient | Trace |

建立連線的主體是資料庫而不是伺服器：伺服器名稱只有連線字串取得到，而那一份
不進通知。設定重新載入分不出是設定頁按下去還是殼層自己推的，因此一律 `Ambient`，
跟著詳細度門檻走而不是無條件顯示。主題筆刷的初始化那一次不追蹤——它是
「初始化 SqlAssist」裡面的一步。

結果格線與片段存放（剖析結果欄位、轉換結果為 JSON、載入片段清單、儲存片段、
還原片段預設）尚未接線。

## 已知缺口

接線時補齊的四項：開發者措辭的標題、建議清單與 QuickInfo 自身沒有 `Begin`、
跨資料庫與連結伺服器沒有專屬回報，以及熱路徑上的內插字串標題。

還沒補上的：等待共享閘期間工作由別人擁有（`SqlMetadataCatalog` 的 `_snapshotGate`
仍然沒有排隊狀態，畫面上也沒有等待中的視覺）；結果格線、片段存放與掃描指令碼宣告
三處尚未接線；工作階段統計是第 4 步，`Trace` 的快取命中目前只進紀錄與記憶體歷史，
還沒有依 `(Kind, Title, Subject)` 累計的報表。
