# 安裝與開始使用

這一頁寫給只想在 SSMS 使用 SqlAssist 的人：裝起來、確認裝好了、知道第一次該試什麼。
不需要任何開發經驗。若要修改原始碼或自行建置，請改看[建置、安裝與偵錯](development.md)。

## 支援範圍

- Windows x64
- SQL Server Management Studio 22.9.x
- 發布頁提供的 `SqlAssist.Ssms22.vsix`

直接安裝發布的 VSIX 不需要 Visual Studio，也不需要 .NET SDK。SSMS 20、21、Azure Data
Studio、Visual Studio Code、macOS 與 Linux 都不是這個擴充的安裝目標。

## 你的資料去了哪裡

建議是在你的機器上算出來的。需要的資料表、欄位、程序與參數資訊，只向**你目前這個
查詢視窗已經連上的那台 SQL Server** 查詢——與你自己在查詢視窗執行 `SELECT` 是同一條
連線、同一組權限，看不到的東西這個擴充一樣看不到。

沒有任何內容送到雲端，也沒有 AI 模型參與；擴充本身不會連上網際網路。設定與診斷紀錄
留在本機的 `%LOCALAPPDATA%\SqlAssist.Ssms22`。

> [!WARNING]
> [SSMS 目前未正式支援第三方擴充套件](https://learn.microsoft.com/en-us/ssms/faq#are-extensions-supported-in-ssms)。
> SqlAssist 會以 SSMS 22.9.x 實機驗證，但 SSMS 更新可能改動內部介面；升級 SSMS 前
> 可先確認最新 Release 的相容性說明。

## 安裝 VSIX

1. 開啟 [GitHub Releases](https://github.com/a73013110/SqlAssist.Ssms22/releases)。
2. 在最新版本的 **Assets** 下載 `SqlAssist.Ssms22.vsix`。不要下載 GitHub 自動產生的
   Source code 壓縮檔；那不是安裝檔。
3. 儲存查詢並關閉所有 SSMS 視窗。
4. 開啟下載的 `.vsix`。
5. 在 VSIX Installer 確認安裝目標為 **SQL Server Management Studio 22**，完成安裝。
6. 重新啟動 SSMS。

如果 Releases 頁面顯示尚無任何版本，代表目前還沒有公開、可直接安裝的 VSIX；此時
只能依[開發文件](development.md)自行建置。

若開啟 `.vsix` 時沒有出現 SSMS 22，可在 PowerShell 明確使用 SSMS 隨附的安裝程式：

```powershell
# 明確使用 SSMS 22 隨附的 VSIX Installer，避免選到其他 Visual Studio 執行個體。
$installer = Join-Path $env:ProgramFiles 'Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe'
& $installer "$HOME\Downloads\SqlAssist.Ssms22.vsix"
```

SSMS 若安裝在自訂位置，請把 `$installer` 改成該安裝目錄下的
`Common7\IDE\VSIXInstaller.exe`。

## 確認是否載入

啟動 SSMS 後，開啟 **工具 → SqlAssist**。選單中應可看到：

- 啟用 SqlAssist
- 顯示即時建議
- 移至定義
- 顯示游標處物件的結構
- 重新整理建議
- 程式碼片段…
- 設定…
- 關於與診斷…

若整個選單不存在，先確認安裝目標與 SSMS 版本，再依本頁的[問題排查](#問題排查)處理。

## 第一次使用

1. **保留 SSMS 的 T-SQL IntelliSense 總開關。** SqlAssist 預設只關閉會打架的內建自動
   建議清單；SSMS 的紅色錯誤波浪線、大綱與參數提示仍會運作。
2. 在查詢視窗連線到資料庫後開始輸入。SqlAssist 會在背景讀取必要的中繼資料；第一次
   尚未命中快取時，下一次觸發建議就會顯示資料庫物件。
3. 按 `Ctrl+,` 開啟設定，搜尋 `SqlAssist`；也可使用 **工具 → SqlAssist → 設定…**。

建議先試這七件事：

| 操作 | 結果 |
|---|---|
| 輸入 `sel` | 建議 `SELECT`；完整輸入 `select` 再按空白也可自動改成大寫 |
| 輸入 `libr` | 以模糊比對尋找類似 `Lib_Reader` 的物件名稱 |
| 在資料來源別名後輸入 `.` | 列出該來源的欄位、型別與主索引鍵 |
| 把游標放在選取清單的 `*` 後按 Tab | 將 `SELECT *` 展開成完整欄位清單 |
| 輸入 `ap` 按 Tab，再選一個預存程序 | 整份可執行的 `ALTER` 定義直接進編輯器，游標停在名稱後面 |
| 在建議清單選取物件後按向右鍵 | 開啟可捲動、可複製的完整結構預覽 |
| 游標停在物件名稱上按 `F12` | 另開查詢視窗顯示可執行的定義，沿用目前連線 |

使用 `↑`、`↓` 選擇建議，按 Tab 或 Enter 提交，按 Esc 關閉。輸入 Snippet 捷徑後可用
Tab／Shift+Tab 在欄位之間移動；完整捷徑表見[程式碼片段](snippets.md)。

## 常用入口

| 想做的事 | 入口 |
|---|---|
| 暫時停用整個擴充 | `Ctrl+Alt+Shift+S`，或 **工具 → SqlAssist → 啟用 SqlAssist** |
| 只關閉自動建議 | **工具 → SqlAssist → 顯示即時建議** |
| 看某個物件到底怎麼寫的 | `F12`，或 **工具 → SqlAssist → 移至定義** |
| 查看游標處物件的完整結構 | `Ctrl+F12`，或 **工具 → SqlAssist → 顯示游標處物件的結構** |
| 資料表剛變更，想重新載入 | `Ctrl+Shift+D`，或 **工具 → SqlAssist → 重新整理建議** |
| 修改功能行為 | `Ctrl+,` 後搜尋 `SqlAssist` |
| 編輯內建或自訂片段 | **工具 → SqlAssist → 程式碼片段…** |
| 回報問題前檢查狀態 | **工具 → SqlAssist → 關於與診斷…** |

## 更新

下載新版 VSIX 後，關閉所有 SSMS 視窗並再次開啟安裝檔即可覆蓋更新。版本與相容性說明
以該次 [Release](https://github.com/a73013110/SqlAssist.Ssms22/releases) 為準。

## 解除安裝

先儲存查詢並關閉所有 SSMS 視窗，再於 PowerShell 執行：

```powershell
# 依 VSIX Identity 只解除安裝 SqlAssist，不會移除 SSMS。
$installer = Join-Path $env:ProgramFiles 'Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe'
& $installer '/uninstall:SqlAssist.Ssms22.7f693af0-846a-4ee8-ab70-a174a3e31f65'
```

解除安裝會保留 `%LOCALAPPDATA%\SqlAssist.Ssms22` 內的設定與診斷紀錄。SSMS 若安裝在
自訂位置，請依安裝步驟的說明調整 `$installer`。

## 問題排查

### 看得到 SqlAssist，但沒有資料庫物件

- 確認目前查詢視窗已連線到資料庫。
- 按 `Ctrl+Shift+D`，或從 **工具 → SqlAssist → 重新整理建議** 重新載入。
- 確認設定「列出資料庫物件與欄位」仍為開啟。
- 權限不足、逾時或連線失敗時，這一輪會退回只有 T-SQL 關鍵字與片段的建議。

### 出現兩份建議清單

- 保留 SSMS 的 T-SQL IntelliSense 總開關。
- 在 SqlAssist 設定確認「只使用 SqlAssist 的建議清單」為開啟。

### 功能整組沒有載入

- 確認使用的是 Windows x64 與 SSMS 22.9.x。
- 關閉所有 SSMS 視窗後重新安裝 VSIX。
- 開啟 **工具 → SqlAssist → 關於與診斷…**，先複製匿名診斷摘要；只有需要逐步追查時才啟用詳細診斷紀錄。

診斷紀錄位於：

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log
```

回報問題時，請附上 SSMS 版本、SqlAssist 版本、重現步驟與複製出的診斷摘要；貼上 SQL 或紀錄前，
請先移除伺服器名稱、資料庫名稱、帳號及公司內部的結構描述與物件名稱。

- [建立 GitHub Issue](https://github.com/a73013110/SqlAssist.Ssms22/issues)
- [功能與設定文件](settings.md)
