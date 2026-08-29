# SqlAssist for SSMS 22

針對 SQL Server Management Studio 22.9.x 開發的 T-SQL 生產力擴充套件。
版本由 git 歷史自動決定，最新版請見 [GitHub Releases](https://github.com/a73013110/SqlAssist.Ssms22/releases)。

輸入時即時建議 T-SQL 關鍵字、內建函式、程式碼片段與資料庫物件，排名採用詞首感知的
模糊比對；按 Tab 把 `SELECT *` 展開成完整欄位清單；滑鼠停留或按向右鍵看得到物件的
完整結構。設定全部走 SSMS 22 的 Unified Settings。

## 功能

| 功能 | 一句話 | 詳細 |
|---|---|---|
| 建議清單 | 由平台原生非同步 IntelliSense 呈現，排名自己做 | [completion.md](docs/completion.md) |
| 關鍵字大寫 | 打完 `select` 按空白就變 `SELECT` | [completion.md](docs/completion.md) |
| 欄位建議 | 輸入 `別名.` 列出該資料來源的欄位、型別與 PK | [completion.md](docs/completion.md) |
| 程式碼片段 | `ssf`、`ap`、`af` 可增刪修，帶佔位符 | [snippets.md](docs/snippets.md) |
| 展開 `SELECT *` | Tab 換成完整欄位清單，三種排法 | [wildcard-expansion.md](docs/wildcard-expansion.md) |
| 物件結構 | 停留提示與可複製的浮動預覽 | [structure-preview.md](docs/structure-preview.md) |

## 文件

| 文件 | 內容 |
|---|---|
| [architecture.md](docs/architecture.md) | 三個專案的分層、資料夾規則、為什麼改用平台原生管線 |
| [completion.md](docs/completion.md) | 建議清單、關鍵字目錄、內建函式、自動大寫、上下文與欄位建議 |
| [snippets.md](docs/snippets.md) | 程式碼片段的格式、佔位符與接續行為 |
| [wildcard-expansion.md](docs/wildcard-expansion.md) | `SELECT *` 展開的判斷、欄位來源與排版 |
| [structure-preview.md](docs/structure-preview.md) | 停留提示、浮動預覽與整個擴充的外觀規則 |
| [settings.md](docs/settings.md) | 設定清單、Unified Settings 的限制與刻意不做成設定的東西 |
| [metadata.md](docs/metadata.md) | 中繼資料的四層按需載入與快取 |
| [development.md](docs/development.md) | 環境需求、建置、測試、安裝、偵錯、診斷 |

改程式之前先看 [CLAUDE.md](CLAUDE.md)：那裡是這個專案踩過坑之後定下來的硬規則。

## 專案結構

```text
src/SqlAssist.Core       netstandard2.0，無 Visual Studio 相依，可完整單元測試
src/SqlAssist.Metadata   netstandard2.0，只依賴 System.Data
src/SqlAssist.Ssms22     net48 VSIX
tests/                   鏡像 src 的資料夾結構
tools/                   建置、安裝、偵錯與關鍵字產生腳本
docs/                    按主題切開的說明文件
```

核心邏輯刻意集中在沒有 Visual Studio 相依的兩個專案，因此排名、解析與中繼資料
對應都可以在不啟動 SSMS 的情況下驗證。目前共 589 項單元測試。
資料夾即命名空間，細節見 [architecture.md](docs/architecture.md)。

## 快速開始

```powershell
Set-Location 'D:\GitProject\SqlAssist.Ssms22'
.\tools\Run-CoreTests.ps1
.\tools\Build-Extension.ps1
.\tools\Install-Extension.ps1
```

需要 Windows x64、SSMS 22.9.x 與 .NET SDK 10.0.400。
安裝前請關閉所有 SSMS 視窗，安裝後重新啟動。
完整說明見 [development.md](docs/development.md)。

**請關閉 SSMS 內建的 T-SQL IntelliSense**，否則兩份建議清單會互相干擾；
設定頁偵測到它還開著時會顯示警告，旁邊就有一鍵關閉的按鈕。

## 目前限制

- 建議項還沒有圖示。原生清單支援 `ImageElement`，只是尚未挑選 moniker。
- 未限定的欄位建議不分子句：`GROUP BY` 之後與 `SELECT` 之後給的是同一份清單。
- 逗號分隔的資料來源清單不會收斂目標：`FROM A a, |` 之後給的是完整清單，
  而不是只有資料表與檢視。逗號之後要判斷還在不在資料來源位置，得把資料表清單的
  文法（AS、資料表提示、衍生資料表、資料表值函式）再剖析一次；
  症狀只是清單偏寬而不是空的，暫時不值得多維護一份文法。
- 尚未依外部索引鍵補完 JOIN 條件。
- **建議清單**尚未支援暫存表、資料表變數、CTE 名稱與跨資料庫參考的欄位：
  輸入 `c.` 時列不出 CTE `c` 的欄位。展開 `SELECT *` 是另一條路徑，
  它讀的是指令碼裡的選取清單，衍生資料表與 CTE 都支援。
- 尚未實作結果格的 `Script as INSERT`、`Copy as IN clause`；功能落地前不提供對應設定。
- 關鍵字目錄只涵蓋保留字加一份非保留字補充清單。非保留字（`FILELISTONLY`、
  `ROWTERMINATOR` 這類）在文法上不是關鍵字，任何剖析器都列不出來，
  要補只能加進產生器的補充清單，或做成程式碼片段。
- 程式碼片段與 SSMS 的 `.snippet` 不互通，也還沒有匯入轉換。
- 佔位符只有預設值，展開後不能用 Tab 在欄位之間巡覽。
- SSMS 目前不正式支援第三方擴充套件，安裝與載入方式需要以實機驗證。
- Unified Settings 的服務型別取自 `Microsoft.Internal.VisualStudio.Interop`，
  那是內部 API。取不到服務時會安靜地回退到內建預設值，但 SSMS 改版有可能讓
  設定變成唯讀。

## 下一階段

1. 依外部索引鍵補完 JOIN 條件。
2. 依子句細分未限定的欄位建議（`GROUP BY` 之後不該出現不可分組的欄位）。
3. 建議項圖示與篩選列（只看資料表、只看欄位）。
4. 結果格的 `Script as INSERT`、`Copy as IN clause`。
