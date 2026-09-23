# SQL Memory：驗證與部署

本頁包含 SQL Memory 的自動驗證入口、證據邊界與 SSMS 實機門檻。核心、儲存與 UI 契約
由[索引](index.md)進入，一般安裝門檻見[部署契約](debug-deployment.md)。
自動測試、元件渲染、封裝 probe 與 SSMS 實機是不同證據，不互相替代。

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

涵蓋範圍看 `tests/` 底下的四個測試專案本身，不在文件重列一份會過時的清單。兩條只有這裡說得出來的約束：VSIX 封裝 probe 解開真正的 VSIX、
不從 NuGet cache 補相依；`SqlMemoryStorageSelfTest` 是 SSMS 診斷命令與 probe 的唯一實作，
兩邊不得各寫一份。

WPF PNG 在 `artifacts/theme-qa/`，是忽略的驗證產物，
不是 SSMS 宿主截圖。文件不永久保存批次提交、逐次測試總數或 handoff 順序。
未載入 SSMS 原生資源的元件測試仍使用系統捲軸；其深色外觀須在宿主驗證，不能以元件 PNG 宣稱通過。

## SSMS 實機門檻

Install／Deploy 的選用規則與一般安裝門檻見[部署契約](debug-deployment.md)。命令資源版號與
schema 版號以程式碼為準；schema 升版後舊的開發測試資料庫會被明確拒絕，由工具窗的重建引導封存後重建。

1. 儲存工作並關閉 SSMS，建置後依[發布與安裝](release.md)安裝本版。
2. 開啟詳細記錄，在用量分頁的「診斷」卡片執行「儲存自我測試」兩次；確認報告的 Isolation 建置及載入路徑。
   關掉詳細記錄後那張卡片不出現。
3. SQL Memory 預設已啟用：全新設定第一次開 SSMS 應出現一則「已開始擷取 SQL Memory」，之後不再出現。
   驗證停止輸入、執行選取／全文及關閉的擷取，確認 `Query.Execute` 可解析。
   同一視窗連按 F5 同一段 SQL 只留一列並顯示「×N」、Preview 列出首次／最後執行；
   改連線（含只改大小寫）、改 SQL、A→B→A、另開視窗各自成列；刪除合併列後重新整理不再出現。
4. 開啟 History／Favorites，測滑鼠與 ↑／↓ 選取、同步 Preview、Enter／雙擊只開新 Query 不執行。
5. 明／暗／高對比切換、窄窗、不同 DPI 螢幕、splitter 拖曳／鍵盤、收合再開、篩選與焦點不位移。
   工具列：範圍列在上、套用查詢視窗連線的那一顆在伺服器左邊（Tooltip 寫出目標）、搜尋列貼著清單，窄窗換行後搜尋列仍在最下面（Search 同）。
   多選：Light／Dark／Blue 與 100／150／200% DPI 下圓形勾選欄只在模式中出現、與名稱對齊；選取工具列與搜尋列
   同高、窄版 300 DIP 放得下；只用鍵盤（Space、Shift+Space、Ctrl+A、Ctrl+C、Esc、Tab 進出工具列）走完一輪；
   複製後貼到 Excel、Word 與記事本各一次；全選在大量 History 下的「已載入 N 筆」、進度與取消都留在
   工具列那一格；動畫關閉時工具列與勾選欄直接出現、複製仍換成打勾。SQL Search 同一輪：勾選欄、全選、
   Ctrl+C 在模式內外的差別，以及篩選按鈕有條件時的強調底框在窄窗收字後仍看得出來。
6. 收藏新增／編輯（同時改名稱、標註與 SQL）／移除、跨程序版本衝突、無連線開新 Query、停用／重新啟用與關閉工具窗。
   編輯器 SQL 著色：輸入、Tab、捲動、選取、復原、中文輸入法組字與深淺主題下顏色都跟查詢視窗一致。
7. 查詢視窗右鍵「新增至收藏…」：有無選取各一次，確認收到的範圍與對話框第一列相符；
   未存檔草稿與 SQL Memory 停用時的狀態也各看一次。
8. 收藏連續編輯數次後開版本歷史：切換版本看差異與全文、開新 Query、複製、回溯後新版本在最上面；
   另測窄窗上下疊放、配額調低後的回收說明、另一個 SSMS 先改收藏時的回溯衝突。
9. 用量分頁：分頁與選單都能開、窄窗分頁不折行、數字與設定上限一致；立即維護、清除紀錄（試算筆數與實刪相符、開著的視窗回復內容留著）、
   清除後切回 History 列已更新；壓縮、備份到新檔與同名覆寫各一次；容量調到接近上限時警示點與容量通知各只出現一次。
   整理或清除途中關掉工具窗：卡片改掛編輯區或下一個 SqlAssist 視窗，結果照樣看得到。
   自我測試成敗都走卡片、報告位置走狀態列；維護進行中按不下去。設定頁的 SQL Memory 分類只剩
   「開啟 SQL Memory 用量…」，按下去帶到同一個分頁。
   在編輯器執行查詢時讓擷取被丟棄（把容量或佇列壓到界線）：卡片只新增一列並以 `×N` 累計，工具窗那一行狀態照舊。
   關掉「SQL Memory」種類開關後重跑一輪：整理成功與回溯靜默，整理失敗仍依「顯示所有種類的失敗」出現，容量警示依「顯示部分成功」出現。
10. 工具 → SqlAssist：選單是純文字（沒有圖示），「檢查更新…」與「關於與診斷 → 檢查更新」各跑一次，
    確認有新版／已是最新／拔掉網路三種結論的卡片；關掉「啟動時檢查有沒有新版本」後重開不再自動檢查。
11. 操作補全、物件預覽、F12、物件總管、結果格線，再重啟 SSMS 重跑自我測試與基本流程。
12. 需要驗證解除安裝時先儲存並關閉 SSMS；只照正式卸載流程，不刪除使用者設定或 SQL。

自我測試資料各在 `%LOCALAPPDATA%\SqlAssist.Ssms22\SqlMemorySelfTest\<唯一識別碼>`，
包含 `report.txt`／`self-test.db`，只用內建圖書館 SQL，不覆寫既有報告。
失敗保留資料供診斷；回報 SSMS／SqlAssist 版本、步驟與報告即可，不需要業務 SQL 或連線字串。
PASS 不保證 native DLL 卸載或所有宿主功能相容；未實測項目仍維持待驗。
