# SQL Memory：儲存契約

產品及擷取見[核心](sql-memory.md)，回收見[維護](sql-memory-maintenance.md)。

## 單一 schema

功能未發行，只有 `SqliteSchema.Create` 一份完整建表 SQL；`user_version=1`、
`application_id=0x534d454d`。不保留開發期間的升級鏈、舊表、別名或 schema fixture。
宿主使用 `%LOCALAPPDATA%\SqlAssist.Ssms22\SQLMemory\SQLMemory.db`，不搬移或刪除早期測試資料。
已有不同身分／版本、外來或損壞資料庫明確拒絕，不自動刪檔；開發測試改用新資料庫路徑。

主要資料表：

| 表 | 責任 |
|---|---|
| StoreInfo | StoreId，綁定所有分頁游標 |
| Documents／Sessions | 文件與一次編輯器生命週期、head、CAS 版本及租約 |
| Contents | 去重 SQL BLOB、長度及至多 240 UTF-16 code units 的列表投影 |
| Revisions／Executions | 不可變版本與獨立執行事件 |
| Recovery／Captures／History | 最新未存檔內容、重送紀錄與有索引的歷史投影 |
| FavoriteQueries | 名稱、說明、scope、目前版本引用與 GUID CAS token |
| Leases／StorageUsage | 程序／維護租約與交易內內容計量 |

新庫在 IMMEDIATE 交易一次建立全部表、索引與 triggers；取得寫鎖後重讀身分／版本，
避免多程序初始化競賽。WAL、外鍵常開；每個操作獨立連線且關閉 pool。
busy timeout 預設 5 秒，可指定 1～60 秒；取消或失敗回復交易，不留部分資料。
失敗以 `QueryMemoryStorageException` 分類：Busy（SQLITE_BUSY／LOCKED，唯一可重試）、Io、Corrupt、
Incompatible、InvalidArgument、InvalidCursor、Constraint、Unknown，並保留 SQLite 主要／延伸錯誤碼。

SQL 以 UTF-16LE BLOB 保存，全文讀取再次驗證 hash／長度。Recovery 替換只回收被替換且無引用的
Content，不在每次寫入跑全庫 GC。日常 `StorageUsage` 由 Contents triggers 維護，不 SUM 全庫。

### 擷取路徑：不變內容不寫、去重不讀整份 BLOB

`QuerySessionState.RecoveryContentId` 帶著目前 Recovery 的 ContentId。`QueryRevisionEngine`
在「沒有新版本、不是執行、且內容的 ContentId 跟它相同」時不產生 Content／Recovery／History
寫入，只有 Session／Captures 兩張輕量表照常前進維持 Sequence／CAS；晚到 idle 仍受
`capture.Sequence <= previous.LastSequence` 擋下。連續 idle 但內容不變因此不再每輪重編碼。

去重命中（`ContentId` 已存在）只比對 `Contents.ContentHash` 與 `Length`，不再讀回整份
`SqlBytes` 比對位元組，命中時也就不必重新編碼 UTF-16LE。SHA-256 碰撞機率遠低於這兩個欄位
本身損毀的機率，後者仍會被擋下並丟出 `InvalidDataException`。只有 `SqlBytes` 本體單獨損毀、
Hash／Length 仍相符時寫入路徑不會發現，但下一次讀取（`ReadContentAsync`）一定重新解碼並
驗證雜湊，仍會擋下——完整性保證從「每次去重命中都驗」改成「下一次讀全文時驗」。

## History 與搜尋

History 用時間 DESC／唯一鍵 DESC keyset，每頁 1～200 筆，多讀一筆判斷續頁。時間是 UTC 半開區間，
Server／Database 精確且區分大小寫；複合索引支援時間、種類與連線篩選。
執行專用 Revision 不再投影為 Draft；正式草稿與當前 Recovery 可以各有一列。

游標含 StoreId、篩選指紋及位置，不接受跨庫／跨篩選重用。同一輪期間不重算「七天前」。
分頁不是資料庫快照，並行新增或移動 scope 後須重新整理。

Search 是區分大小寫的字面子字串，不是萬用字元或 FTS。null／空字串停用搜尋，空白是有效內容。
History 只比對 SQL；Favorites 比對名稱、說明或 SQL 的聯集。SQL 以 KMP 搜尋完整 BLOB，
不只查列表摘要；大量未篩選搜尋仍需掃描候選，不承諾固定延遲。

連線 facets 獨立分組，不限於已載入清單，不讀 SQL。依時間或名稱排序，一次 100 個名稱及一個續頁訊號，
offset 續讀；History 不受搜尋／期間限制，Favorites 限定 scope，Database 隨 Server 收斂。

## Favorites

`IFavoriteQueryRepository` 由 SQLite／Hosting 實作。名稱 1～200 字元且不可全空白；說明最多 2000 字元。
SQL 不放 metadata，而由 `CurrentRevisionId` 找 Contents。

- `WriteFavoriteQueryAsync`：null expectedVersion 只允許新增，更新必須符合 GUID token。
  新增、更新引用與連線在同一交易；Revision 不存在由外鍵拒絕。成功更換 token，刪除後重建不重用。
- `DeleteFavoriteQueryAsync`：token 不符或不存在回 Conflict；移除收藏不刪 History、Revision 或 Content。
- `EditFavoriteQuerySqlAsync`：只改 SQL，交易內建立 `FavoriteQueryEdit` Revision，再換目前引用與 token；
  不寫 History、Capture、head 或新 Session。沿用原版本的 Session，連線取自收藏自己的 scope。
- SQL 編輯的 ParentRevisionId 留空，避免版本鏈永久保護全部舊 SQL。Revisions.FavoriteQueryId 只標記歸屬，
  不設外鍵，讓移除收藏不改寫版本；舊版本依維護配額回收。
- 收藏操作不以 CaptureId 冪等。回應不明先重讀，不盲目重送；過期更新不留下部分寫入。

Global 不帶連線，Server 只指定 Server，Database 兩者必填；scope 不合併父層，也不是執行連線。
Favorites 以 FavoriteQueryId DESC keyset，索引涵蓋 Scope／Server／DatabaseName／Id 及版本引用；
收藏不存在額外排序或保護例外。

## 隔離載入

SSMS 必須使用 `IsolatedQueryMemoryRepository`，不得直接建立 SQLite repository。
專用 net48 AppDomain 使用 `QueryMemory.Sqlite.config` 隔離 provider 靜態狀態及 binding redirects，
不修改 `Ssms.exe.config`、不替換宿主 provider、不在 VSIX 夾帶 BCL。

`QueryMemoryRemotingScope` 只在 repository 存活期間解析已載入且完整名稱符合的 Hosting／Core，
涵蓋 SSMS 在 ApplicationBase 外 LoadFrom 的回程 DTO；失敗與 Dispose 解除解析，不接管 SQLite／BCL。
native DLL 仍是程序層級；來源檢查失敗即拒絕，AppDomain 不保證 native 卸載或所有宿主功能共存。

worker 以 `SqliteStorageErrors` 轉成不帶 inner exception 的分類例外，分類與錯誤碼跨 AppDomain 保留。
SQLite repository 只有同步方法，僅 Hosting 與測試可見；隔離層每個操作只排一次背景，操作間不互斥，
Dispose 拒絕新操作並等進行中的操作離開才卸載。取消以 operation id 觸發 worker 端的 token，
中止提交前檢查點與 KMP 掃描；已提交照常回傳結果，VACUUM 開始後不可中斷。
UI 仍須用選取／宿主世代拒絕晚到結果。
版本與授權以 csproj、隔離 config 及 `ThirdPartyLicenses.txt` 為準；更新 provider 必須重跑[封裝驗證](sql-memory-validation.md)。
