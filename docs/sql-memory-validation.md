# SQL Memory：驗證與部署

自動測試、元件渲染、封裝 probe 與 SSMS 實機是不同證據，不互相替代。
核心／儲存／UI 契約見[索引](index.md)，一般安裝門檻見[部署契約](debug-deployment.md)。

## 自動流程

在專案根目錄執行：

```powershell
./tools/Run-CoreTests.ps1
./tools/Build-Extension.ps1
./tools/Test-SqlMemoryPackage.ps1 `
  -VsixPath src/SqlAssist.Ssms22/bin/x64/Release/net48/SqlAssist.Ssms22.vsix `
  -ProbePath tools/SqlAssist.SqlMemory.Probe/bin/x64/Release/net48/SqlAssist.SqlMemory.Probe.exe
./tools/Test-CommandTableToolbar.ps1
./tools/Check-Docs.ps1
./tools/Check-TextFiles.ps1
```

- Core：內容精確性、版本引擎、背景佇列、分頁世代、收藏標註／CAS、版本時間軸、行級差異、維護策略、容量分級與清理串接。
- SQLite：完整新 schema、並行初始化、交易／取消／引用保護、全文搜尋、連續執行合併、版本時間軸、用量計數、試算與清除、備份、游標及 EXPLAIN 索引。
  不再保留未發行舊 schema 的 migration 測試。
- WPF：共用卡片、清單操作、時間軸列、差異標記與底色、主從收合／比例、篩選、用量頁與量表及實際文字／圖示位置；
  Light／Dark／High Contrast 及彩色主題、320／440／740 DIP、100%／150%／200% 渲染。
- VSIX build 內含套件檢查；封裝 probe 解開真正 VSIX，不從 NuGet cache 補相依。
  覆蓋 net48 x64 隔離載入、外部 LoadFrom、雙程序 40 次提交、缺檔／錯誤 native 架構／夾帶 BCL。
- `SqlMemoryStorageSelfTest` 是 SSMS 診斷命令與 probe 的唯一實作，涵蓋重送、重開、執行合併跨 AppDomain、全文、
  Favorites CRUD／SQL 編輯／版本時間軸、配額／租約、檔案釋放及宿主 provider 汙染檢查。

WPF PNG 在 `artifacts/theme-qa/`，是忽略的驗證產物，
不是 SSMS 宿主截圖。文件不永久保存批次提交、逐次測試總數或 handoff 順序。
未載入 SSMS 原生資源的元件測試仍使用系統捲軸；其深色外觀須在宿主驗證，不能以元件 PNG 宣稱通過。

## SSMS 實機門檻

命令資源版號現為 29；Install／Deploy 的選用規則與一般安裝門檻見[部署契約](debug-deployment.md)。
schema 版號現為 5，舊的開發測試資料庫會被明確拒絕，由工具窗的重建引導封存後重建。

1. 儲存工作並關閉 SSMS，建置後依[發布與安裝](release.md)安裝本版。
2. 開啟詳細記錄，執行「SQL Memory 儲存自我測試…」兩次；確認報告的 Isolation 建置及載入路徑。
3. 啟用 SQL Memory，驗證停止輸入、執行選取／全文及關閉的擷取，確認 `Query.Execute` 可解析。
   同一視窗連按 F5 同一段 SQL 只留一列並顯示「×N」、Preview 列出首次／最後執行；
   改連線（含只改大小寫）、改 SQL、A→B→A、另開視窗各自成列；刪除合併列後重新整理不再出現。
4. 開啟 History／Favorites，測滑鼠與 ↑／↓ 選取、同步 Preview、Enter／雙擊只開新 Query 不執行。
5. 明／暗／高對比切換、窄窗、不同 DPI 螢幕、splitter 拖曳／鍵盤、收合再開、篩選與焦點不位移。
6. 收藏新增／編輯（同時改名稱、標註與 SQL）／移除、跨程序版本衝突、無連線開新 Query、停用／重新啟用與關閉工具窗。
   編輯器 SQL 著色：輸入、Tab、捲動、選取、復原、中文輸入法組字與深淺主題下顏色都跟查詢視窗一致。
7. 查詢視窗右鍵「新增至收藏…」：有無選取各一次，確認收到的範圍與對話框第一列相符；
   未存檔草稿與 SQL Memory 停用時的狀態也各看一次。
8. 收藏連續編輯數次後開版本歷史：切換版本看差異與全文、開新 Query、複製、回溯後新版本在最上面；
   另測窄窗上下疊放、配額調低後的回收說明、另一個 SSMS 先改收藏時的回溯衝突。
9. 用量分頁：分頁與選單都能開、窄窗分頁不折行、數字與設定上限一致；立即維護、清除紀錄（試算筆數與實刪相符、開著的視窗回復內容留著）、
   清除後切回 History 列已更新；壓縮、備份到新檔與同名覆寫各一次；容量調到接近上限時警示點與狀態列提醒只出現一次。
10. 操作補全、物件預覽、F12、物件總管、結果格線，再重啟 SSMS 重跑自我測試與基本流程。
11. 需要驗證解除安裝時先儲存並關閉 SSMS；只照正式卸載流程，不刪除使用者設定或 SQL。

自我測試資料各在 `%LOCALAPPDATA%\SqlAssist.Ssms22\SqlMemorySelfTest\<唯一識別碼>`，
包含 `report.txt`／`self-test.db`，只用內建圖書館 SQL，不覆寫既有報告。
失敗保留資料供診斷；回報 SSMS／SqlAssist 版本、步驟與報告即可，不需要業務 SQL 或連線字串。
PASS 不保證 native DLL 卸載或所有宿主功能相容；未實測項目仍維持待驗。
