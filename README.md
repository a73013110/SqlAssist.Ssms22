# SqlAssist for SSMS 22

針對 SQL Server Management Studio 22.9.x 開發的 T-SQL 生產力擴充套件技術驗證專案。
目前版本為 **0.4.1**。

## 目前功能

在 SSMS 查詢編輯器輸入第一個字元後，立即顯示整合建議視窗：

- T-SQL 關鍵字
- Snippet
- 目前資料庫的 Table、View、Procedure、Function、Schema
- 選取項目的 SQL 定義或欄位預覽

主要流程：

```text
s     → 顯示 SELECT、SET 等關鍵字及符合的資料庫物件
ss    → 依名稱模糊篩選資料庫物件與 Snippet
ssf   → 選取 SELECT * FROM Snippet
Tab   → 插入 SELECT * FROM，接著立即顯示 Table／View 清單
```

可使用 `↑`、`↓` 選擇，使用 `Tab` 或 `Enter` 提交，使用 `Esc` 關閉。

內建 Snippet：

| 輸入 | 展開結果 |
|---|---|
| `ssf` | `SELECT * FROM ` |
| `ap` | `ALTER PROCEDURE ` |
| `af` | `ALTER FUNCTION ` |
| `select` | `SELECT` |
| `from` | `FROM` |

快捷詞不會在 SQL 字串、註解、雙引號識別字或方括號識別字內展開。

## 軟開關

安裝 0.4.1 後，可從下列選單立即切換，不需要重新啟動 SSMS：

```text
工具
└─ SqlAssist
   ├─ 啟用 SqlAssist
   ├─ 即時建議
   ├─ Tab 快捷展開
   ├─ 關鍵字轉大寫
   ├─ Procedure／Function 選擇器
   ├─ 結果格命令
   ├─ 顯示診斷狀態
   ├─ 重新整理建議
   └─ 開啟設定
```

全域開關快捷鍵：

```text
Ctrl+Alt+Shift+S
```

設定檔位置：

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\settings.json
```

設定採用同目錄暫存檔原子取代，避免 SSMS 中止時只寫入半份 JSON。

建議視窗設定範例：

```json
{
  "suggestions": {
    "enabled": true,
    "triggerAfterCharacters": 1,
    "maximumItems": 100,
    "showPreview": true,
    "delayMilliseconds": 70,
    "qualifyObjectNames": false,
    "useSquareBrackets": false
  }
}
```

## 環境需求

- Windows x64
- SQL Server Management Studio 22.9.x
- Visual Studio 18／2026 或相容的 MSBuild
- .NET SDK 10.0.400

## 建置

```powershell
Set-Location 'D:\GitProject\SqlAssist.Ssms22'
.\tools\Run-CoreTests.ps1
.\tools\Build-Extension.ps1
```

預期輸出：

```text
src\SqlAssist.Ssms22\bin\Release\net48\SqlAssist.Ssms22.vsix
```

## 安裝技術驗證版

先關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Install-Extension.ps1
```

安裝程式會開啟 SSMS 隨附的 `VSIXInstaller.exe`，請在畫面中確認安裝目標為
**SQL Server Management Studio 22**。安裝後重新啟動 SSMS，建立查詢視窗並測試
`ssf`、`ap`、`af` 或 `select` 後按 `Tab`。

## 解除安裝

先儲存查詢並關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Uninstall-Extension.ps1
```

預設會顯示 SSMS 的 VSIXInstaller 確認介面。如需無介面模式：

```powershell
.\tools\Uninstall-Extension.ps1 -Quiet
```

解除安裝只移除 VSIX，會保留 `%LOCALAPPDATA%\SqlAssist.Ssms22` 內的設定與診斷紀錄。

## 目前限制

- 目前使用自製 WPF 建議視窗，外觀與 SQL Prompt 尚未完全一致。
- 尚未實作完整圖形化 Options 頁面，目前進階設定以 `settings.json` 管理。
- 尚未處理別名後方的欄位建議，例如 `t.`。
- 尚未實作結果格的 `Script as INSERT`、`Copy as IN clause`。
- SSMS 目前不正式支援第三方擴充套件；安裝與載入方式需要以實機驗證。

## 診斷紀錄

診斷功能會在下列位置記錄是否載入 SQL 編輯器及是否收到 `Tab`：

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log
```

也可以執行：

```powershell
.\tools\Show-Diagnostics.ps1
```

## 下一階段

1. 完整圖形化 Options／Snippet Manager。
2. Alias、欄位、JOIN condition 建議。
3. 結果格的 `Script as INSERT`、`Copy as IN clause`。
