# SQL Memory：收藏版本歷史與回溯

收藏列的**版本歷史**開啟單一對話框：左側（窄於 760 DIP 時在上）是版本時間軸，右側是選中版本的差異或全文。
清單與 Preview 的其他操作見 [UI](sql-memory-ui.md)，版本怎麼產生見[儲存](sql-memory-storage.md)。

## 讀取契約

- `ISqlFavoriteStore.ReadFavoriteRevisionsAsync` 以 (CreatedAt, RevisionId) 新到舊 keyset 分頁，沿用
  `SqlMemoryPage`；游標綁 StoreId 與 FavoriteId，換收藏沿用回 InvalidCursor。收藏不存在回空頁。
- 版本順序不走 ParentRevisionId：收藏版本刻意不串版本鏈。SQLite 以 `INDEXED BY IX_Revisions_Favorite`
  反向串流，不做暫存排序，由 EXPLAIN 測試守住。
- 列表項只帶 RevisionId、ContentId、CreatedAt、Reason、是否目前版本、單行 Preview 與長度，不讀全文。
- 目前版本可能引用自 History、不屬於這個收藏；它照時間序併入同一份分頁，同一快照內讀取。
- DTO 經 `IsolatedSqlMemoryStore` 跨 AppDomain，自我測試涵蓋跨界續頁。

## 回溯

回溯不另設寫入路徑：讀出舊版本全文，交給 `SaveFavoriteAsync` 另存一筆新版本並設為目前版本。
不改寫也不刪除任何版本，CAS 衝突、不進 History 與配額回收全部沿用改 SQL 的語意。

- 目前版本、內容與目前版本相同或內容已清理的版本不能回溯；按鈕說明原因。
- 先以 `SqlAssistConfirmationWindow` 確認，取消是預設；說明文字交代「另存新版本，不是倒帶」與配額回收。
- Conflict 或回應不明都不重送，重讀收藏與時間軸；成功後新版本出現在最上面並被選取。
- 對話框關閉時回報收藏可能已變，清單列重讀；提交期間不允許關閉。

## 誠實的保留範圍

每個收藏只保留 `sqlAssist.sqlMemory.maxFavoriteRevisions` 版，舊版本由[維護](sql-memory-maintenance.md)回收。
對話框第一列與清單頁尾都說明這不是完整編輯史；版本數達到配額時明講更舊的已回收。
`ReadContentAsync` 回 null 的版本標為「內容已清理」，不能預覽、比對、複製或回溯。

## 差異比對

`Core/SqlMemory/SqlTextDiff` 是與 UI 無關的行級差異：去掉共同頭尾後以 Myers O(ND) 求最短編輯腳本。
逐行序數比較，CR／LF／CRLF 都是行界；只差換行字元時明講，不說成相同。

- 上限：每側 100 萬字元、去頭尾後 2 萬行、編輯距離 1000。超過就把中段整段取代，並在面板說明原因。
- 比對在背景執行並可取消；切換版本以 220 ms 去彈跳讀取，選取識別與宿主世代擋住晚到結果。
- 非目前版本與目前版本比；目前版本與前一版比；最早保留的版本沒有比較對象。

## 呈現

- 時間軸列沿用卡片節奏：軌道與圓點（目前版本實心強調色、其他空心）、時間、單行 SQL、來源與長度。
- 列操作、快捷選單與差異面板都由 `SqlFavoriteRevisionCommand.All` 建立：預覽全文、開新 Query、複製、
  回溯；回溯用收藏語意色調並隔開。執行在 `SqlFavoriteRevisionCommands`。
- `SqlTextDiffView` 是可重用的虛擬化差異表：+／- 標記、舊／新行號與原文，捲到第一處變更。
  新增與刪除底色由 `ThemePalette` 推導，SQL 前景與標記在兩種表面都過 4.5:1；高對比不上色，只靠標記。
- 新列（續頁、回溯後）用 SQL Memory 卡片的揭露動畫；切換版本時差異以共用 120 ms 淡入出現。
  兩者都受全域動畫設定控制並走 RenderTransform／Opacity，不改版面尺寸。捲軸都是原生樣式。
