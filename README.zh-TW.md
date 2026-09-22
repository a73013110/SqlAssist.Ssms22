# SqlAssist for SSMS 22

**在 SSMS 22 中補全與展開 SQL、預覽物件結構、搜尋資料庫物件，並找回與收藏查詢。**

[繁體中文](README.zh-TW.md) · [English](README.md)

[![Release](https://img.shields.io/github/v/release/a73013110/SqlAssist.Ssms22?sort=semver)](https://github.com/a73013110/SqlAssist.Ssms22/releases)
[![License](https://img.shields.io/github/license/a73013110/SqlAssist.Ssms22)](LICENSE)
![SSMS 22.9.x](https://img.shields.io/badge/SSMS-22.9.x-5c2d91)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078d4)

<p align="center"><img src="docs/images/hero.png" width="900" alt="SSMS 22 查詢編輯器中的 SqlAssist 建議清單與物件結構資訊"></p>

SqlAssist 是安裝於 **SQL Server Management Studio 22** 的 VSIX，不是另一套編輯器。
建議完全在本機計算；結構資訊只向目前連線的 SQL Server 查詢，不經雲端，也沒有 AI 模型參與。

[下載 VSIX](https://github.com/a73013110/SqlAssist.Ssms22/releases) ·
[安裝與開始使用](docs/getting-started.md) · [文件索引](docs/index.md) ·
[回報問題](https://github.com/a73013110/SqlAssist.Ssms22/issues)

## 功能展示

以下動畫以虛構的 `LibraryDB` 重建介面，並非錄影或效能依據；每段均附文字與靜態圖。
[操作播放器：播放、暫停與重播](https://a73013110.github.io/SqlAssist.Ssms22/demos/feature-demos.html)。

### 依資料庫與語句位置補全

輸入 `libr` → 按 **→** 預覽欄位 → 按 **Tab** 提交 `Lib_Reader`。建議會依語句位置收斂，
並支援模糊比對、別名、指令碼變數、暫存表與即時欄位資訊。

<p align="center"><img src="docs/images/completion-preview-demo.gif" width="820" alt="預覽 Lib_Reader 欄位後按 Tab 提交補全"></p>

[查看靜態圖](docs/images/completion-preview-demo.png)

### 按 Tab 展開重複 SQL

在 `*` 後按 **Tab** 即可展開明確欄位，也能依中繼資料產生 `INSERT`、`EXEC`、`MERGE`
或 `ALTER` SQL；本例使用「永遠每欄一行」設定。

<p align="center"><img src="docs/images/expand-star-demo.gif" width="820" alt="按 Tab 將 SELECT 星號展開為 Lib_Reader 明確欄位"></p>

[查看靜態圖](docs/images/expand-star-demo.png)

[觀看 INSERT 動畫](docs/images/insert-template-demo.gif)：按 Tab 產生欄位與型別預留值，並略過 IDENTITY。
[查看靜態圖](docs/images/insert-template-demo.png)

| `INSERT` | `EXEC` |
|:---:|:---:|
| <img src="docs/images/expand-insert-into.png" width="400" alt="前後對照：提交 INSERT 目標後產生欄位與依型別填入的 VALUES 預留值"> | <img src="docs/images/expand-exec.png" width="400" alt="前後對照：提交 EXEC 目標後產生具名參數清單"> |
| **`MERGE`** | **`ALTER PROCEDURE / FUNCTION`** |
| <img src="docs/images/expand-merge-into.png" width="400" alt="前後對照：提交 MERGE 目標後產生安全且可編輯的 MERGE 骨架"> | <img src="docs/images/expand-def-procedure.png" width="400" alt="前後對照：提交 ALTER 目標後載入完整物件定義"> |

### 不離開查詢視窗即可看懂物件

輸入 `libr` 並選中 `Lib_Reader`，按 **→** 展開下方結構預覽，再切到「指令碼」頁籤；
不必離開目前查詢視窗，即可查看欄位、索引、鍵值、參數與完整 DDL。

<p align="center"><img src="docs/images/structure-preview-demo.gif" width="820" alt="從建議清單展開 Lib_Reader 結構預覽並切換到指令碼頁籤"></p>

[查看靜態圖](docs/images/structure-preview-demo.png)

也可以在 `Loan` 按 **F12**，沿用目前連線開啟完整定義，但不執行 SQL。

<p align="center"><img src="docs/images/f12-definition-demo.gif" width="820" alt="F12 開啟 Loan 的鍵值、索引與物件說明"></p>

[查看 F12 靜態圖](docs/images/f12-definition-demo.png)

### 搜尋物件名稱、定義與欄位

在 **Search** 輸入 `CopyNo`，選取結果後以 **›** 切換定義命中；可依連線、種類與位置篩選，
但不搜尋資料列內容。

<p align="center"><img src="docs/images/sql-search-demo.gif" width="820" alt="SQL Search 找到 CopyNo 並切換定義命中"></p>

[查看靜態圖](docs/images/sql-search-demo.png)

### 找回 SQL，收藏後再次使用

在 **History** 找到 `Loan`，按 **☆** 收藏，再到 **Favorites** 雙擊開啟；
新查詢沿用目前連線，但不執行 SQL。

<p align="center"><img src="docs/images/sql-memory-demo.gif" width="820" alt="從 History 收藏查詢後在 Favorites 重新開啟"></p>

[查看靜態圖](docs/images/sql-memory-demo.png)

### 立即重用查詢結果

選取格線，右鍵複製成 `IN` 條件，再貼到 `WHERE` 後。選單也能產生 `#temp`、Markdown、
JSON、欄位剖析與完整儲存格內容。

<p align="center"><img src="docs/images/result-in-demo.gif" width="820" alt="將所選 CopyNo 去重並複製成 IN 條件"></p>

[查看靜態圖](docs/images/result-in-demo.png)

### 用片段包住既有 SQL

選取 SQL，從「以片段包住選取範圍」搜尋並套用 `ifb`，再填寫條件並按 **Tab**；
套用不會執行 SQL。

<p align="center"><img src="docs/images/surround-snippet-demo.gif" width="820" alt="將所選 SQL 包成可編輯的 IF 片段"></p>

[查看靜態圖](docs/images/surround-snippet-demo.png)。另有括號與引號自動配對，以及可個別調整的功能開關。

## 安裝

需要 **Windows x64** 與 **SSMS 22.9.x**。

1. 從最新 [GitHub Release](https://github.com/a73013110/SqlAssist.Ssms22/releases) 下載 `SqlAssist.Ssms22.vsix`。
2. 儲存查詢、關閉所有 SSMS 視窗，再執行 VSIX 安裝程式。
3. 重啟 SSMS；看到「工具 → SqlAssist」即代表載入成功。

> [!IMPORTANT]
> 保持 SSMS 內建 T-SQL IntelliSense 開啟；SqlAssist 只抑制會互相干擾的自動建議清單。

> [!WARNING]
> [SSMS 目前未正式支援第三方擴充套件](https://learn.microsoft.com/en-us/ssms/faq#are-extensions-supported-in-ssms)；
> 本專案以 SSMS 22.9.x 實機驗證。

## 深入了解

[開始使用](docs/getting-started.md)說明安裝與更新；[文件路由](docs/index.md#主題)涵蓋所有功能、
設定與開發主題。貢獻者請先讀 [CLAUDE.md](CLAUDE.md)。專案採用 [Apache License 2.0](LICENSE)。
