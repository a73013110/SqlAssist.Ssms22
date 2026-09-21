# SQL Memory：儲存契約

產品及擷取見[核心](sql-memory.md)，回收見[維護](sql-memory-maintenance.md)。

## 單一 schema

功能未發行，只有 `SqliteSchema.Create` 一份完整建表 SQL；`user_version=5`、
`application_id=0x534d454d`。不保留開發期間的升級鏈、舊表、別名或 schema fixture。
宿主使用 `%LOCALAPPDATA%\SqlAssist.Ssms22\SQLMemory\SQLMemory.db`，不搬移或刪除早期測試資料。
已有不同身分／版本、外來或損壞資料庫明確拒絕，不自動刪檔；由使用者在工具窗按下重建才封存。
封存把 `.db`、`-wal`、`-shm` 當成一組更名為同目錄的 `.bak.<時間戳>`：只搬主庫會讓新資料庫套上舊 WAL，
主庫已不在、只剩 `-wal`／`-shm` 的殘局同樣要搬。中途失敗全部搬回原位，唯一實作在 `SqlMemoryDatabaseArchive`。

主要資料表：

| 表 | 責任 |
|---|---|
| StoreInfo | StoreId，綁定所有分頁游標 |
| Documents／Sessions | 文件與一次編輯器生命週期、head、CAS 版本及租約 |
| Contents | 去重 SQL BLOB、長度及至多 240 UTF-16 code units 的列表投影 |
| Revisions／Executions | 不可變版本與獨立執行事件；版本屬於一個 Session 或一個 Favorite，兩者皆空由 CHECK 擋下 |
| Recovery／Captures／History | 最新未存檔內容、重送紀錄與有索引的歷史投影；連線只存在投影 |
| Favorites | 名稱、說明、伺服器／資料庫標註、目前版本引用、最後儲存時間與 GUID CAS token |
| Leases／StorageUsage／MaintenanceState | 租約、內容計量與共用維護輪次 |

新庫在 IMMEDIATE 交易一次建立全部表、索引與 triggers；取得寫鎖後重讀身分／版本，
避免多程序初始化競賽。WAL、外鍵常開；每個操作獨立連線且關閉 pool。
busy timeout 預設 5 秒，可指定 1～60 秒；取消或失敗回復交易，不留部分資料。
失敗以 `SqlMemoryStorageException` 分類：Busy（SQLITE_BUSY／LOCKED，唯一可重試）、Io、Corrupt、
Incompatible、InvalidArgument、InvalidCursor、Constraint、Unknown，並保留 SQLite 主要／延伸錯誤碼。

SQL 以 UTF-16LE BLOB 保存，全文讀取重算 ContentId（即雜湊）與長度驗證。
沒有讀取端的欄位不進 schema：版本、執行與 Recovery 不各存一份連線，文件不存路徑。Recovery 替換只回收被替換且無引用的
Content，不在每次寫入跑全庫 GC。日常 `StorageUsage` 由 Contents triggers 維護，不 SUM 全庫。

### 擷取路徑：不變內容不寫、去重不讀整份 BLOB

`SqlSessionHead.RecoveryContentId` 帶著目前 Recovery 的 ContentId。`SqlCapturePlanner`
在「沒有新版本、不是執行、且內容的 ContentId 跟它相同」時不產生 Content／Recovery／History
寫入，只有 Session／Captures 兩張輕量表照常前進維持 Sequence／CAS；晚到 idle 仍受
`capture.Sequence <= previous.LastSequence` 擋下。連續 idle 但內容不變因此不再每輪重編碼。

去重命中（`ContentId` 已存在）只比對 `Length`，不讀回 `SqlBytes`，也不重新編碼 UTF-16LE；
長度損毀丟出 `InvalidDataException`。只有 `SqlBytes` 本體單獨損毀時寫入路徑不會發現，
下一次 `ReadContentAsync` 重算雜湊一定擋下——完整性保證是「下一次讀全文時驗」。

## History 與搜尋

分頁、游標、搜尋語意與掃描預算見[搜尋](sql-memory-search.md)。

`DeleteHistoryAsync` 在 IMMEDIATE 交易刪投影與它自己的本體：執行列刪併進它的全部 Executions、Recovery 刪該列；
版本只在沒有保護根與其他引用時刪除，引用清單與維護共用 `SqliteContentRows`。釋出的 Content 同交易回收。
不存在或不屬於該 Session 回 NotFound；仍開著的 Session 下一次擷取照常重寫 Recovery。

## Favorites

`ISqlFavoriteStore` 由 SQLite／Isolation 實作，方法層級契約見該介面的 XML 註解。
SQL 不放 metadata，而由 `CurrentRevisionId` 找 Contents；GUID CAS token 刪除後重建不重用。

- `SaveFavoriteAsync` 是唯一寫入：新增、改資料、改 SQL 與回溯都在一個交易完成並更新 UpdatedAt。
  沒有 SQL 就引用 `CurrentRevisionId` 指定的既有版本（不存在由外鍵拒絕）；有 SQL 才建收藏自己的版本。
  預期版本 null 只允許新增，收藏已存在回 Conflict；否則 token 不符或不存在回 Conflict，不留部分寫入。
- `DeleteFavoriteAsync`：token 不符或不存在回 Conflict；移除收藏不刪 History、Revision 或 Content。
- 收藏自己的 `Favorite` Revision 不屬於任何 Session：不進 History、不建 Capture、不動 head 或序號，
  ParentRevisionId 留空以免版本鏈永久保護全部舊 SQL。Revisions.FavoriteId 只標記歸屬、不設外鍵，
  移除收藏不改寫版本；舊版本依配額回收，時間軸見[版本歷史](sql-memory-revisions.md)。
- 收藏操作不以 CaptureId 冪等，回應遺失後先重讀。

伺服器與資料庫是各自選填的標註，不是階層也不是執行連線；空白正規化為 NULL，schema 另擋空字串。

篩選帶的是**一組名稱**（`SqlConnectionNames`：去空白、去重、排序，空名單表示不限），History、
Favorites 與連線名稱面板三份請求共用同一份正規化——游標指紋照名單組，沒有排序去重的話同一組
條件會因為勾選先後算出兩個指紋，而續頁那一刻會被當成換過條件。SQL 只有一個名稱時用 `=`，
多個時用 `+欄位 IN (…)`：一元 `+` 讓這個條件不能當成索引限制，查詢回到時間索引上邊走邊濾。
不加的話 SQLite 會挑 `IX_History_ServerTime` 這類索引，而 `ORDER BY` 落在索引後段的欄位上，
它改為建一棵暫存 b-tree 排序整份結果——單頁掃描預算限制得了讀進來的 BLOB，限制不了那一次排序。
兩種形狀都由 `SqliteHistoryTests` 的 EXPLAIN 測試守住。
清單以 (UpdatedAt, FavoriteId) DESC keyset，四個時間索引對應 History 的同一組連線篩選，
篩選 SQL 與游標由 `SqliteConnectionFilter`／`SqliteTimeCursor` 共用。

## 隔離載入

SSMS 必須使用 `IsolatedSqlMemoryStore`，不得直接建立 SQLite store。
專用 net48 AppDomain 使用 `SqlMemory.Isolation.config` 隔離 provider 靜態狀態及 binding redirects，
不修改 `Ssms.exe.config`、不替換宿主 provider、不在 VSIX 夾帶 BCL。

`IsolationAssemblyResolveScope` 只在 store 存活期間解析已載入且完整名稱符合的 Isolation／Core，
涵蓋 SSMS 在 ApplicationBase 外 LoadFrom 的回程 DTO；失敗與 Dispose 解除解析，不接管 SQLite／BCL。
native DLL 仍是程序層級；來源檢查失敗即拒絕，AppDomain 不保證 native 卸載或所有宿主功能共存。

worker 以 `SqliteStorageErrors` 轉成不帶 inner exception 的分類例外，分類與錯誤碼跨 AppDomain 保留。
SQLite store 只有同步方法，僅 Isolation 與測試可見；隔離層每個操作只排一次背景，操作間不互斥，
Dispose 拒絕新操作並等進行中的操作離開才卸載。取消以 operation id 觸發 worker 端的 token，
中止提交前檢查點與 KMP 掃描；已提交照常回傳結果，VACUUM 開始後不可中斷。
UI 仍須用選取／宿主世代拒絕晚到結果。
版本與授權以 csproj、隔離 config 及 `ThirdPartyLicenses.txt` 為準；更新 provider 必須重跑[封裝驗證](sql-memory-validation.md)。
