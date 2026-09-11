# Query Memory：核心契約與實作進度

SQL History、Draft Recovery 與 Saved Queries 共用內容儲存，但保持各自的生命週期。
返回[文件路由](index.md)。

## 已實作：純核心

程式位於 `src/SqlAssist.Core/QueryMemory/`，測試鏡像於 `tests/SqlAssist.Core.Tests/QueryMemory/`。
目前不註冊 SSMS 事件、不啟用 SQL 擷取，也沒有正式持久化 provider 或 UI。

- `QueryDocument` 不綁連線；`QuerySession` 區隔每次編輯器生命週期。
- `QueryContent` 精確雜湊 UTF-16LE code units；內容位址含演算法前綴。不正規化空白、
  換行或大小寫，也不使用 delta chain。雜湊只用固定大小的暫存區。
- `QueryRevisionEngine` 產生不可變交易計畫。高頻 idle 更新每 Session 一份 Recovery；
  內容改變且跨過設定間隔才新增 auto revision。手動快照即使相同 SQL 仍建立新版本。
- Execution 獨立於 Revision。相同完整 SQL 或連續相同選取 SQL 重用版本；連線以執行
  當下為準。選取版本不改文件 head，也不覆蓋整份文件的 Recovery。
- `QueryMemoryPolicy` 是宿主設定的不可變輸入；未把容量與保留天數預設寫死在 Domain。
- `SavedQuery` 與 scope 先定義資料形狀；編輯、搜尋與保存流程尚未實作。

## 儲存層必須履行的契約

`IQueryMemoryRepository` 不暴露 SQLite 型別。`CommitAsync` 必須用單一交易完成內容去重、
新增版本／執行事件、Session head 更新及 Recovery 替換／刪除。

- 先依 CaptureId 判斷重送，再 CAS Session.Version；衝突不能留下部分寫入。
- ContentId 有唯一索引；相同 hash 要核對原文，碰撞必須失敗，不能覆寫其他 SQL。
- Session.Sequence 對所有擷取事件遞增，不使用時間或編輯器文字版本當排序依據。
  新 SSMS instance 必須建立新 SessionId；既有文件可共用 DocumentId。
- 正式關閉先保存最終版本，再於同一交易刪除 Recovery。重送或較舊 idle 不得重新開啟 Session。
- 列表 API 有上限與 opaque cursor。以時間、唯一鍵做 keyset paging，游標綁定篩選條件；
  列表預覽至多 240 個 UTF-16 code units，全文另行讀取。
- ConnectionIdentity 禁止放完整連線字串、密碼或 Token。

Recording repository **只存在測試專案**，不代表 SQLite 交易、索引與多程序安全已驗證。

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

1. **SQLite 封裝 spike**：獨立 storage 專案，驗證 net48、SSMS x64、VSIX native runtime、
   安裝後載入與卸載；通過前不啟用擷取。不能以開發機可載入取代實際 VSIX 驗證。
2. **持久化**：schema migration、WAL／busy handling、交易與重送、跨程序寫入、損壞降級、
   有索引的搜尋分頁；新增真實 SQLite integration tests。
3. **容量與 Saved Queries**：設定驅動 retention、儲存上限、分批清理孤立 Content；
   保護所有 Saved Query、Pinned 與 Manual Snapshot 的引用，再實作 Saved Query CRUD／scope。
   活動 Recovery 與 Session head 不得被清理成懸空引用。
4. **SSMS 擷取**：Session tracker、可調 idle debounce、可靠的 Execute submitted 訊號與
   選取範圍、Close／卸載 flush、連線快照。不得改變現有殼層命令熱路徑護欄。
5. **設定與 UI**：先提供啟用與隱私說明，再接 History／Saved Queries、分頁虛擬化列表、
   延遲全文預覽、恢復與開新 Query。預覽重用既有 SQL viewer／Chrome，不另造通用視窗框架。

建議起始值留給設定批次：idle 5 秒、auto revision 10 分鐘、draft 30 天、execution 10,000 筆、
每 Session auto revision 50 筆；儲存上限提供 256 MB／512 MB／1 GB／不限。
