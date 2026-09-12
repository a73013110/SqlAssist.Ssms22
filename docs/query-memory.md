# Query Memory：核心契約與實作進度

SQL History、Draft Recovery 與 Saved Queries 共用內容儲存，但保持各自的生命週期。
返回[文件路由](index.md)。

## 已實作：純核心

程式位於 `src/SqlAssist.Core/QueryMemory/`，測試鏡像於 `tests/SqlAssist.Core.Tests/QueryMemory/`。
目前不註冊 SSMS 事件、不啟用 SQL 擷取，也沒有 UI。已加入 [SQLite 儲存與隔離載入](query-memory-storage.md)。

- `QueryDocument` 不綁連線；`QuerySession` 區隔每次編輯器生命週期。
- `QueryContent` 精確雜湊 UTF-16LE code units；內容位址含演算法前綴。不正規化空白、
  換行或大小寫，也不使用 delta chain。雜湊只用固定大小的暫存區。
- `QueryRevisionEngine` 產生不可變交易計畫。高頻 idle 更新每 Session 一份 Recovery；
  內容改變且跨過設定間隔才新增 auto revision。手動快照即使相同 SQL 仍建立新版本。
- Execution 獨立於 Revision。相同完整 SQL 或連續相同選取 SQL 重用版本；連線以執行
  當下為準。選取版本不改文件 head，也不覆蓋整份文件的 Recovery。
- `QueryMemoryPolicy` 是宿主設定的不可變輸入；未把容量與保留天數預設寫死在 Domain。
- `SavedQuery` 已有 [CRUD／scope 與引用保護](query-memory-saved.md)；SQL 編輯、搜尋與 UI 保存流程尚未實作。

## 儲存層必須履行的契約

`IQueryMemoryRepository` 不暴露 SQLite 型別。`CommitAsync` 必須用單一交易完成內容去重、
新增版本／執行事件、Session head 更新及 Recovery 替換／刪除。

- 先依 CaptureId 判斷重送，再 CAS Session.Version；衝突不能留下部分寫入。
- ContentId 有唯一索引；相同 hash 要核對原文，碰撞必須失敗，不能覆寫其他 SQL。
- Session.Sequence 對所有擷取事件遞增，不使用時間或編輯器文字版本當排序依據。
  新 SSMS instance 必須建立新 SessionId；既有文件可共用 DocumentId。
- 正式關閉先保存最終版本，再於同一交易刪除 Recovery。重送或較舊 idle 不得重新開啟 Session。
- History API 有上限與 opaque cursor。以時間、唯一鍵做 keyset paging，游標綁定篩選條件；
  列表預覽至多 240 個 UTF-16 code units，全文另行讀取。
- ConnectionIdentity 禁止放完整連線字串、密碼或 Token。

Recording repository **只存在核心測試專案**；SQLite 的真實交易與分頁另有 net48 integration tests。

## 背景工作與接線責任

`QueryMemoryBackgroundWriter.TryEnqueue` 只接收不可變快照及政策，不讀全文、不做 I/O。
同 Session 尚未消費的 idle 可以合併；Execute、Close、Manual 是合併屏障。
筆數與估計 SQL 位元組都有上限，包含處理中的項目。位元組是文字估計，不是程序記憶體硬上限；
SSMS 快照可能另保留編輯器內部資料。

`QueueFull`／`SnapshotTooLarge` 明確拒絕新項目，不偷偷刪除已接受的執行事件。宿主必須提供
可見的降級提示。儲存失敗使 `Completion` fault 並停止接收，不能把排空當保存成功。
卸載時 await `CompleteAsync`；處理程序被強制結束仍可能失去尚未落盤的內容。

`QueryMemoryProcessor` 只對 CAS 衝突做有界重試；一般 I/O 錯誤與取消保留給呼叫端。
接線層需在背景完成後回報狀態，不能在編輯器命令前同步等待保存。

## 後續批次與驗收門檻

1. **SSMS 實機載入**：SQLite、隔離 AppDomain、VSIX 缺檔／架構與跨程序測試已具備；
   真正 SSMS 已回報[自我測試通過](query-memory-validation.md)，宿主共存／重啟等仍待回報，不啟用擷取。
2. **儲存維護**：已有 schema v3 與逐版 migration、WAL、busy timeout、交易／重送、損壞拒絕與索引分頁；
   後續 schema 升級仍需逐版 migration 測試，不可重建使用者資料庫。
3. **容量與 Saved Queries**：已有 [Saved CRUD](query-memory-saved.md)及[有界維護](query-memory-maintenance.md)。
   期限清理／容量量測已具備；設定驅動排程、筆數配額與實體整理仍待後續批次。
4. **SSMS 擷取**：Session tracker、可調 idle debounce、可靠的 Execute submitted 訊號與
   選取範圍、Close／卸載 flush、連線快照。不得改變現有殼層命令熱路徑護欄。
5. **設定與 UI**：先提供啟用與隱私說明，再接 History／Saved Queries、分頁虛擬化列表、
   延遲全文預覽、恢復與開新 Query。預覽重用既有 SQL viewer／Chrome，不另造通用視窗框架。

建議起始值留給設定批次：idle 5 秒、auto revision 10 分鐘、draft 30 天、execution 10,000 筆、
每 Session auto revision 50 筆；儲存上限提供 256 MB／512 MB／1 GB／不限。
