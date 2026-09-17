# SQL Memory 共用元件

範圍：SQL Memory 在 Core、SQLite／隔離層與 Ssms22 的唯一出處。其他功能見
[共用元件表](shared-components.md)與[平台共用元件](shared-components-platform.md)。

## Core

| 這件事 | 唯一出處 |
| --- | --- |
| SQL 內容位址、版本取樣與交易式儲存契約（含連線 facets） | `Core/SqlMemory/SqlContent.cs`、`SqlCapturePlanner.cs`、`ISqlHistoryStore.cs` |
| 開啟／關閉、世代、寫入器故障、心跳、維護排程與使用者主動整理 | `Core/SqlMemory/SqlMemoryRuntime.cs`（設定轉政策在 `SqlMemoryConfiguration.cs`） |
| 清單的篩選轉請求、分頁世代與選取還原 | `Core/SqlMemory/SqlMemoryBrowserModel.cs` |
| 查詢視窗的文件／Session 身分與多重選取的執行文字 | `Core/SqlMemory/SqlDocumentIdentity.cs`、`SqlSelectionText.cs` |
| 有界背景佇列與交易衝突重試 | `Core/SqlMemory/SqlCaptureQueue.cs`、`SqlCaptureCommitter.cs` |
| 收藏儲存、標註正規化、版本時間軸與版本衝突契約 | `Core/SqlMemory/ISqlFavoriteStore.cs`（儲存與隔離層共用） |
| 收藏版本時間軸的分頁世代、比較對象、能否回溯與保留說明 | `Core/SqlMemory/SqlFavoriteRevisionTimeline.cs` |
| 兩份文字的行級差異（上限與整段取代降級） | `Core/SqlMemory/SqlTextDiff.cs` |
| 有界維護、容量、用量報表、手動清理與備份契約 | `Core/SqlMemory/ISqlMemoryMaintenanceStore.cs`（儲存與隔離層共用） |
| 手動清理與立即維護的批次串接、釋出容量 | `Core/SqlMemory/SqlMemoryCleanup.cs` |
| 容量比例、分級門檻與通知防抖 | `Core/SqlMemory/SqlMemoryCapacity.cs` |
| 用量頁與清除試算的數字、分級、健康狀態與文案 | `Core/SqlMemory/SqlMemoryUsageSummary.cs` |
| 本次工作階段的清理紀錄 | `Core/SqlMemory/SqlMemoryActivityLog.cs` |

## SQLite 與隔離層

| 這件事 | 唯一出處 |
| --- | --- |
| SQLite 隔離載入 | `SqlMemory.Isolation/IsolatedSqlMemoryStore.cs` |
| History／Favorites 的連線篩選 SQL、時間 keyset 游標與分頁讀取 | `SqlMemory.Sqlite/SqliteFilters.cs` 的 `SqliteConnectionFilter`、`SqliteKeysetPage.cs` |
| 刪除一列 History 的交易步驟（逐筆刪除與手動清理共用） | `SqlMemory.Sqlite/SqliteHistoryRows.cs`（引用條件在 `SqliteContentRows.cs`） |
| 宿主／封裝儲存自我測試 | `SqlMemory.Isolation/SqlMemoryStorageSelfTest.cs` |

## Ssms22

| 這件事 | 唯一出處 |
| --- | --- |
| 資料庫檔案的整組封存（`.db`／`-wal`／`-shm`） | `Ssms22/SqlMemory/SqlMemoryDatabaseArchive.cs` |
| 收藏新增與編輯（資料、標註與 SQL 一次儲存） | `Ssms22/SqlMemory/FavoriteEditorWindow.cs` |
| 伺服器／資料庫標註輸入與出現過的名稱 | `Ssms22/SqlMemory/SqlConnectionTagInput.cs` |
| 設定、計時器、狀態列與容量提醒接線（邏輯在 Core 的 `SqlMemoryRuntime`） | `Ssms22/SqlMemory/SqlMemoryHost.cs` |
| 列操作清單與執行（卡片、快捷選單、Preview 共用） | `Ssms22/UI/SqlMemoryList.cs` 的 `SqlMemoryRowCommand`、`Ssms22/SqlMemory/SqlMemoryItemCommands.cs` |
| 清單的鍵盤、續頁、右鍵選取與列按鈕派送 | `Ssms22/UI/SqlMemoryList.cs` 的 `SqlMemoryListBase<TAction>` |
| 等儲存的使用者操作：拒絕重入與宿主世代檢查 | `Ssms22/SqlMemory/SqlMemoryOperationGate.cs` |
| 收藏版本操作清單與執行（時間軸列、快捷選單、差異面板共用） | `Ssms22/UI/SqlFavoriteRevisionList.cs` 的 `SqlFavoriteRevisionCommand`、`Ssms22/SqlMemory/SqlFavoriteRevisionCommands.cs` |
| 列上的幽靈操作按鈕（卡片與時間軸共用） | `Ssms22/UI/SqlAssistChrome.SqlMemory.cs` 的 `CreateRowActionButton` |
| 行級差異的虛擬化呈現（標記、行號、語意底色） | `Ssms22/UI/SqlTextDiffView.cs`（樣板在 `SqlAssistChrome.Revisions.cs`） |
| 清單頁尾 | `Ssms22/UI/SqlMemoryPager.cs`（狀態與文案在 `SqlMemoryBrowserModel.Footer`） |
| 量表（分級色、長度動畫、不確定進度） | `Ssms22/UI/SqlUsageMeter.cs` |
| 用量分頁與警示點 | `Ssms22/UI/SqlAssistChrome.SqlMemory.cs` 的 `CreateMemoryUsageTab`、`SetUsageBadge` |
| 清除紀錄的條件、試算摘要與頁尾（試算接線在 `SqlMemory/SqlMemoryCleanupWindow.cs`） | `Ssms22/UI/SqlMemoryCleanupView.cs` |
