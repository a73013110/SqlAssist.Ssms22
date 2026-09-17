# SQL Memory：用量與手動整理

用量頁是 SQL Memory 工具窗 History／Favorites 之後的第三個分頁「Usage」，SqlAssist 選單的「SQL Memory 用量」帶到同一分頁。
沒有自己的頁首：重新整理與設定沿用工具列，搜尋、篩選與「目前連線」只屬於清單分頁，切過來時收起。
保留規則與背景回收見[維護](sql-memory-maintenance.md)，外觀遵守 [UI 準則](ui-guidelines.md)：容量主卡片之下每個區塊是 `CreateCardSection` 卡片。

## 讀取

- `ReadUsageReportAsync` 在一個讀取交易內取得內容量、`.db`／WAL 大小、`freelist_count × page_size`
  可回收空間、各類筆數、History 伺服器前 5 名與最早／最新時間。
- 計數只走索引、不讀 BLOB；伺服器分組沿 `IX_History_ServerTime` 前綴，由 EXPLAIN 測試守住。
  只在切到分頁、重新整理與整理完成時讀，不進排程。儲存停用或換了一份時整頁收起，原因留給宿主狀態與狀態列。
- 分類只列筆數不列容量：內容去重後共用，相加會超過總量。伺服器分布計 History 列，含回復內容投影。
- 比例、分級、健康標題與文案只在 `SqlMemoryUsageSummary`。70% 起 Warning、90% 起 Critical，
  維護結論 `CannotReclaimWithinPolicy` 直接 Critical；容量不限時不畫量表。
- 上次／下次維護與分級來自本程序的 `SqlMemoryRuntime.MaintenanceOverview`，共用輪次級數讀 `MaintenanceState`。

## 整理動作

| 動作 | 行為 |
|---|---|
| 立即維護 | 日常級政策的一次性批次（不帶 Claim）巡完一輪，`RequiresAnotherPass` 最多 4 輪；不升級、不寫共用輪次 |
| 清除紀錄… | 見下節 |
| 壓縮資料庫 | 確認後走設定頁命令同一條 `CompactAsync` |
| 備份… | `VACUUM INTO` 到不存在的檔案；同名由原生存檔對話框確認後先刪，儲存層拒絕既有檔案與目前資料庫 |
| 開啟資料夾 | 沿用復原卡片 |

每批 200 個候選、一個 IMMEDIATE 交易，擷取與讀取插在批次之間；有刪除才截斷 WAL。
操作走 `SqlMemoryOperationGate`：期間動作全停用、內容上方顯示不確定進度。
結果寫在工具窗狀態列並提醒壓縮後才縮檔，完成後重讀快照。維護與清除結束時清單已載入的列視為過期：
還在用量分頁就等切回清單分頁才重讀，已切回則立即重讀，兩者都保留選取；壓縮與備份不動紀錄，不重讀。

## 清除紀錄

- 對象：執行紀錄、已有版本的草稿、已關閉視窗的回復內容、收藏舊版本（每收藏保留 1～50 個，預設 10）。
- History 類依期間（7／30／90／365 天以前或全部）與伺服器、資料庫精確篩選；收藏版本不看期間。
- History 類由 `SqliteHistoryRows` 逐列刪除，與逐筆刪除共用保護根與引用清單；
  候選由新到舊沿時間索引讀，(時間, 鍵) 游標綁 StoreId 與條件。
- 回復內容只清沒有租約的 Session。本程序還沒有心跳租約時 Runtime 直接拒絕，
  否則自己開著的視窗在儲存層也像已關閉。
- 收藏版本走只帶每收藏配額的一次性維護批次，目前版本受保護。
- 試算與清除共用候選條件，收藏版本以 `rank()` 對齊維護「第 N 新時間、只刪更舊」的界線。
  試算是上限：共用內容與版本鏈在刪除交易內才重查。
- 對話框改條件後 250 ms 重算；頁尾左側寫「最多清除 N 筆」與分類明細（文案在 `SqlMemoryUsageSummary`），
  清除按鈕寫出筆數。取消是預設與初始焦點；清除是語意色主要動作，條件一改就停用到新試算回來，
  所以不再疊第二層確認框。它是[對話框規範](ui-dialogs.md)的範本。

## 清理紀錄與警示

- `SqlMemoryActivityLog` 只在記憶體保留最新 20 筆：手動操作的成功與失敗，以及有刪除的背景維護（30 分鐘內合併）。
- `SqlMemoryCapacityMonitor` 觀測維護批次、用量頁與手動整理回報的容量；分級改變觸發 `CapacityChanged`，
  用量分頁圖示的警示點在 Warning／Critical 顯示，出現時單次縮放 240 ms。
- 剛進入 Critical 時狀態列提醒一次，降到 80% 以下才重新武裝；停用或換儲存時分級歸零。

## 動畫與配色

量表以 `ScaleTransform` 由舊值滑到新值 360 ms，控制項在重新整理之間沿用；內容首次出現淡入 120 ms；
不確定進度只在可見且動畫開啟時滑動。全部受全域動畫設定控制，關閉時直接落在終值。
`MeterNormal`／`MeterWarning`／`MeterCritical` 由強調色、收藏色與危險色推導並維持 3:1 圖形對比，
高對比改用前景色；分級一定同時有文字與百分比。
