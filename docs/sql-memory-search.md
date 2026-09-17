# SQL Memory：History 分頁與搜尋

資料表與交易見[儲存](sql-memory-storage.md)，UI 呈現見[互動](sql-memory-ui.md)。

## 分頁

History 用時間 DESC／唯一鍵 DESC keyset，每頁 1～200 筆，多讀一筆判斷續頁。時間是 UTC 半開區間，
Server／Database 精確且區分大小寫；複合索引支援時間、種類與連線篩選。
執行專用 Revision 不再投影為 Draft；正式草稿與當前 Recovery 可以各有一列。

游標含 StoreId、篩選指紋及位置，不接受跨庫／跨篩選重用。同一輪期間不重算「七天前」。
Favorites 以最後儲存時間走同一種 keyset 與游標；分頁不是資料庫快照，並行編輯後須重新整理。

## 連續執行合併

同一 Session 的上一筆執行列與本次 ContentId、Server、DatabaseName 都相同（區分大小寫，與篩選一致；
兩邊都沒有連線也算相同）時，提交交易更新那一列而不新增：CreatedAt 改為本次時間、`ExecutionCount` 加一，
`FirstExecutedAt` 與列鍵不變，`RevisionId` 改指本次執行的版本。

- 在寫入時合併，不在讀取時分組：分組會讓 keyset 游標、頁大小與掃描預算不再對應實際讀到的列。
- 只併連續執行：A→B→A 是三列；其他 Session 一律不併。中間的草稿不打斷合併，合併列會移到它之上。
- 上一筆執行列以 `Sessions.LatestExecutionEntryKey` 主鍵直查，不掃表；列已被刪除或回收就另起新列。
- `Executions` 仍每次執行一列，以 `EntryKey` 外鍵指回投影；刪除見[儲存](sql-memory-storage.md)，
  期限與配額見[維護](sql-memory-maintenance.md)。

連線 facets 獨立分組，不限於已載入清單，不讀 SQL。依時間或名稱排序，一次 100 個名稱及一個續頁訊號，
offset 續讀；History 讀投影、Favorites 讀標註，兩邊都不受搜尋／期間限制，Database 隨 Server 收斂。

## 搜尋語意

Search 是區分大小寫的字面子字串，不是萬用字元或 FTS。null／空字串停用搜尋，空白是有效內容。
History 只比對 SQL；Favorites 比對名稱、說明或 SQL 的聯集。SQL 以 KMP 掃完整 UTF-16LE BLOB，
名稱與說明以 ordinal 比對；兩者都是 UTF-16 code unit 語意，未配對 surrogate 也照字面比。

## 掃描預算

比對在讀取迴圈逐列進行，不放進 WHERE。每頁最多檢查 2000 列候選或 16 MiB SQL，先到者為準：

- 預算用盡且還有候選：回傳已命中的列（可少於頁大小或為空），`IsSearchPartial` 為 true，
  游標接在最後檢查過的候選之後，並回 `SearchedThrough`（該候選的時間）。
- 在預算內掃完就沒有游標，不留只會回空頁的續頁；湊滿一頁照常續頁。
- 游標格式與指紋沿用一般分頁，部分搜尋與載入更多共用同一種游標。
- 候選查詢必須沿時間索引串流、不得有暫存排序，否則第一列出來前已讀完所有 BLOB；
  EXPLAIN 測試逐一確認實際分頁 SQL。
- 單列不中途切斷：超過位元組預算的單份 SQL 仍整份比對，取消在列與列之間生效。
- 不會因預算用盡自動續搜；是否往前找由呼叫端決定。

## 未採用 FTS5 trigram 預過濾

`SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 內建的 SQLite 3.53.4 已編入 FTS5，`trigram case_sensitive 1`
與 `contentless_delete=1` 可用；不採用的理由：

- 少於 3 個字元無法用 trigram，常見三字組（如 `SEL`）的候選近乎全表，仍要回到有上限的掃描；延遲上界不因此出現。
- FTS 只收 TEXT，UTF-16 BLOB 要另轉一份文字；未配對 surrogate 轉 UTF-8 會失真，仍須 KMP 驗證。
- trigram 索引每個字元位置都有一筆項目，另佔可觀空間，卻不在只計 Contents 的 `StorageUsage` 內；容量上限會低估實際檔案。
- 每個新 Content 多一次 FTS 寫入，擷取提交變慢；維護回收 Content 也要同步刪索引。
