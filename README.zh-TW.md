# SqlAssist for SSMS 22

**在 SSMS 22 原生 T-SQL 編輯器中，提供理解目前資料庫的即時建議、SQL 展開與物件預覽。**

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

### 依資料庫與語句位置補全

建議會依 `SELECT`、`FROM`、`JOIN`、`EXEC` 等位置收斂；支援詞首／CamelHump 模糊比對、
別名欄位、指令碼變數、暫存表、即時欄位資訊與關鍵字大小寫。

<p align="center"><img src="docs/images/completion.png" width="820" alt="SSMS 22 中依語句位置顯示物件候選、比對字元與即時欄位資訊"></p>

### 按 Tab 展開重複 SQL

在 `*` 後按 Tab，即可換成排版完成的明確欄位。提交 `INSERT`、`EXEC`、`MERGE` 或
`ALTER` 目標時，也能依中繼資料產生可直接修改的 SQL。

<p align="center"><img src="docs/images/expand-star.png" width="820" alt="前後對照：在 SELECT 星號後按 Tab，展開成格式化的明確欄位清單"></p>

| `INSERT` | `EXEC` |
|:---:|:---:|
| <img src="docs/images/expand-insert-into.png" width="400" alt="前後對照：提交 INSERT 目標後產生欄位與依型別填入的 VALUES 預留值"> | <img src="docs/images/expand-exec.png" width="400" alt="前後對照：提交 EXEC 目標後產生具名參數清單"> |
| **`MERGE`** | **`ALTER PROCEDURE / FUNCTION`** |
| <img src="docs/images/expand-merge-into.png" width="400" alt="前後對照：提交 MERGE 目標後產生安全且可編輯的 MERGE 骨架"> | <img src="docs/images/expand-def-procedure.png" width="400" alt="前後對照：提交 ALTER 目標後載入完整物件定義"> |

### 不離開查詢視窗即可看懂物件

直接預覽欄位、索引、鍵值、參數與可複製的 DDL；按 F12 會沿用目前連線，在新查詢視窗
開啟完整定義。

<p align="center"><img src="docs/images/structure-preview.png" width="820" alt="浮動物件預覽顯示欄位、索引、鍵值與可複製的資料表 DDL"></p>

### 立即重用查詢結果

從結果格線選單產生 `#temp` 指令碼或 `IN` 條件、複製 Markdown 或 JSON、剖析欄位，並查看
被 SSMS 格線截斷的單格完整內容。

<p align="center"><img src="docs/images/result-grid-utility.png" width="820" alt="SSMS 結果格線選單提供暫存表、IN、Markdown、JSON、欄位剖析與完整內容功能"></p>

此外還有具備 Tab Stop 導航的內建 T-SQL 片段、括號與引號自動配對，以及可個別調整的功能開關。

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
設定與開發主題。貢獻者請先讀 [CLAUDE.md](CLAUDE.md)。專案採用 [MIT License](LICENSE)。
