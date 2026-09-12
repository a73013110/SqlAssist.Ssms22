# Query Memory：SQLite 儲存與封裝

核心契約見[查詢記憶](query-memory.md)。目前已完成儲存與隔離載入驗證，**未啟用 SQL 擷取**。

## 分層與相依

- `SqlAssist.QueryMemory.Sqlite`：netstandard2.0，實作 `IQueryMemoryRepository`。
- `SqlAssist.QueryMemory.Hosting`：net48，以專用 AppDomain 隔離 provider 靜態狀態與版本轉向。
- `SqlAssist.Core.QueryMemory`：DTO 可跨隔離邊界傳遞；不參考 SQLite、VS 或 SSMS。
- `SqlAssist.QueryMemory.Probe`：僅用於工具驗證，不放入 VSIX。

鎖定 Microsoft.Data.Sqlite.Core 10.0.12、SQLitePCLRaw 3.0.5、SQLite 3.53.4。
授權全文隨 VSIX 的 `ThirdPartyLicenses.txt` 提供。

[Microsoft 說明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)指出 SQLite 的
async ADO.NET 方法仍同步執行。本實作明確在背景做 I/O，不把 `Async` 方法名稱當成背景保證。

## 交易、去重與查詢

- schema 有 application_id、user_version 與 StoreId；交易內重讀版本，避免雙程序初始化競賽。
  未知／未來版本、不相干 SQLite 與損壞檔案直接失敗，不自動刪除或重建。
- WAL、外鍵與 IMMEDIATE 寫交易；每次操作獨立連線，不保留 connection pool。
  busy 等待預設 5 秒，可由建立 repository 的參數調整至 1～60 秒。
- CaptureId 重送判斷在 CAS 之前；衝突、SQL 例外或取消不留下部分交易。
- SQL 只存 UTF-16LE BLOB，避免 UTF-8 轉換破壞未配對 surrogate 或 NUL。
  同內容以唯一鍵去重並核對原文；全文讀取再驗證 hash／長度。
- Recovery 替換時只清理被替換、已無任何引用的 Content，不在每次寫入做全庫 GC。
- History 是有索引的列表投影；執行專用 Revision 不額外顯示為 Draft。
  正式 Draft 版本與目前 Recovery 可各有一列，Recovery 每 Session 只有一列。
- Server／Database 採精確比對；時間是 UTC 半開區間。複合索引支援時間及連線分頁。
  cursor 包含 StoreId、篩選指紋與時間／唯一鍵，不接受跨儲存庫或跨篩選重用。
- Search 是區分大小寫的字面子字串。KMP 搜尋完整 BLOB，不限於 240 字元預覽；
  它不是 FTS，無篩選的大量全文搜尋仍需掃描候選內容。列表不載入全部 SQL。

## 為什麼需要專用 AppDomain

封裝 spike 發現：VSSDK 不會自動封裝反射載入的 SQLitePCLRaw 相依；SSMS 的
`Ssms.exe.config` 又把 SQLitePCLRaw.core 導向宿主版本。
直接使用新 bundle 會遇到載入失敗，或與宿主共用 provider 靜態狀態。

因此 SSMS 接線必須使用 `IsolatedQueryMemoryRepository`，不可直接建立 Sqlite repository。
它使用擴充內的 `QueryMemory.Sqlite.config`，**不修改 Ssms.exe.config、不替換宿主 provider**。
BCL 仍從 SSMS 取得，不在 VSIX 夾帶 System.*。更新 SQLitePCLRaw 時，必須同步核對隔離設定內的
組件版本並重跑封裝測試，不能只改 NuGet 版號。

SSMS 從 ApplicationBase 外載入擴充，代理回程的按名稱解析會造成 `InvalidCastException`。
`QueryMemoryRemotingScope` 在 repository 存活期間只解析已載入、完整名稱相符的 Hosting／Core；
初始化失敗或 Dispose 時解除，不接管 SQLite／BCL。這是 [CLR 載入內容差異](https://learn.microsoft.com/en-us/dotnet/framework/deployment/best-practices-for-assembly-loading)
的邊界處理，不透過複製 DLL 到 SSMS IDE 或修改宿主設定規避。

跨 AppDomain 的 DTO 複製、SQL、雜湊均在背景進行。此邊界會增加大型 SQL 的暫存記憶體，
writer 的文字容量估計不是程序記憶體硬上限。隔離呼叫只在派送前接受取消；派送後以交易結果為準。
先排空 BackgroundWriter，再於背景 Dispose 隔離 repository；不得在 UI 執行緒同步卸載。
AppDomain 不隔離程序層級的 native DLL；載入來源檢查失敗即拒絕使用，仍須實測宿主共存。

## 驗證方式與尚未通過的門檻

執行完整測試與建置後，在專案根目錄執行：

```powershell
./tools/Test-QueryMemoryPackage.ps1 `
  -VsixPath src/SqlAssist.Ssms22/bin/x64/Release/net48/SqlAssist.Ssms22.vsix `
  -ProbePath tools/SqlAssist.QueryMemory.Probe/bin/x64/Release/net48/SqlAssist.QueryMemory.Probe.exe
```

工具解開真正 VSIX，以宿主設定及 BCL 在獨立 net48 x64 程序驗證隔離載入、兩程序同時提交
40 次執行、內容去重、程序結束後檔案釋放；負向案例檢查缺檔、錯誤 native 架構與夾帶 BCL。
工具也會執行與 SSMS 命令共用的 `QueryMemoryStorageSelfTest`；宿主操作見[實機驗收](query-memory-validation.md)。
另以不含 SqlAssist DLL 的啟動目錄搭配 `LoadFrom` 重跑，涵蓋代理跨載入內容的回歸案例。
不從 NuGet cache 或建置目錄補 DLL，不安裝／解除安裝擴充。紀錄留在 `artifacts/`。

SSMS 安裝後載入已回報通過；[驗收狀態](query-memory-validation.md)區分已驗證與待確認項目。
目前未接編輯器事件，也未完成 retention、Saved Queries CRUD、設定或 History UI。
