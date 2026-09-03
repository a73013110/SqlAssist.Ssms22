# SqlAssist for SSMS 22

**把 SSMS 22 的 T-SQL 編輯器補上「懂你目前這個資料庫」的即時建議、SQL 展開與物件預覽。**

[繁體中文](README.zh-TW.md) · [English](README.md)

[![Release](https://img.shields.io/github/v/release/a73013110/SqlAssist.Ssms22?sort=semver)](https://github.com/a73013110/SqlAssist.Ssms22/releases)
[![License](https://img.shields.io/github/license/a73013110/SqlAssist.Ssms22)](LICENSE)
![SSMS 22.9.x](https://img.shields.io/badge/SSMS-22.9.x-5c2d91)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078d4)

<p align="center">
  <img src="docs/images/hero.png" width="900"
       alt="編輯器裡的游標連到一張建議清單卡片；清單每一列前面是物件種類圖示，名稱開頭幾個字元以亮色標出，代表比對命中的位置">
</p>

SqlAssist 是安裝在 **SQL Server Management Studio 22** 裡的 VSIX 擴充套件，不是另一套
SQL 編輯器，也不是 SSMS 的修改版——你仍然在原本的查詢視窗裡工作。

建議完全在你的機器上算出來：需要的資料表、欄位與程序資訊，只向**你已經連上的那台
SQL Server** 查詢，不經過任何雲端服務，也沒有 AI 模型參與。

[安裝與開始使用](docs/getting-started.md) ·
[下載 VSIX](https://github.com/a73013110/SqlAssist.Ssms22/releases) ·
[回報問題](https://github.com/a73013110/SqlAssist.Ssms22/issues) ·
[開發者文件](docs/index.md)

---

## 核心體驗：即時自動建議（Auto-complete）

只要開始輸入，建議清單就會依據目前語句的位置自動過濾候選項目。選取任一項目時，下方立即展示該物件的欄位、資料型別與主鍵標記，不用切換視窗去查。

<p align="center">
  <img src="docs/images/completion.png" width="820"
       alt="SELECT * FROM Li 之後彈出建議清單，列出 Libraries、LibraryBranches 等資料表；下方面板顯示 dbo.Libraries 的七個欄位、型別與旗標">
</p>

- **詞首與縮寫模糊比對**：支援 CamelHump 與詞首匹配。記得用途、記不全全名時，打幾個關鍵字母就能找到（例如打 `cb` 找到 `Cat_BookCopy`，打 `libr` 找到 `Lib_Reader`）。
- **即時欄位與型別面板**：在建議清單中反白任何資料表或檢視，下方連動顯示欄位清單、資料型別與 `PK`、`NOT NULL` 屬性。
- **上下文感知過濾**：游標在 `FROM` 或 `JOIN` 後優先推薦資料表與檢視；在 `SELECT`、`WHERE`、`ORDER BY` 後優先推薦欄位；在 `EXEC` 後推薦預存程序。
- **別名感知**：輸入 `lr.` 即自動解析該別名指向的資料來源，列出其所屬欄位。
- **指令碼變數即時解析**：查詢中剛宣告的 `@變數`、`#temp` 暫存表與 `@table` 變數會在本地即時解析，不必等伺服器重新整理中繼資料就能列入建議。
- **輸入時關鍵字自動大寫**：打完關鍵字自動轉換為標準大寫，保持指令碼風格一致。

---

## 萬用字元與語句展開

不再需要手動敲打大量重複的欄位清單與結構骨架。

### 1. `SELECT *` 展開為明確欄位

游標停在 `*` 後面按 `Tab`，自動展開成逐行縮排整齊的明確欄位清單。多表查詢時會自動加上別名前綴，避免效能問題與日後欄位異動的風險。

<p align="center">
  <img src="docs/images/expand-star.png" width="820"
       alt="上半是 SELECT * FROM dbo.Books，游標停在星號後面；按下 Tab 之後，下半變成逐行列出 BookId、ISBN、Title 等九個欄位的 SELECT">
</p>

### 2. 提交時展開成整句

在特定語法情境下選取物件，直接補齊整份語句骨架：

- **`INSERT INTO`**：選取資料表後按 Enter，自動產生欄位清單與帶有型別預設值的 `VALUES` 區塊。
- **`EXEC`**：選取預存程序後，自動帶出所有具名參數、型別註解與必要的 `OUTPUT` 變數。
- **`ALTER PROCEDURE / FUNCTION`**：打 `ap` 或 `af` 選取物件，整份可執行的定義直接載入編輯器，游標停在名稱後方，原地修改、原地執行。
- **`MERGE INTO`**：選取資料表後，自動展開 `USING ... ON ... WHEN MATCHED` 的完整範本。

---

## 物件結構預覽與移至定義

想確認物件細節或閱讀底層程式碼，不必在左側「物件總管」的層層目錄中翻找。

<p align="center">
  <img src="docs/images/structure-preview.png" width="820"
       alt="浮動結構預覽的指令碼分頁，顯示 dbo.LibraryAnnouncement 的 CREATE TABLE 與後面接著的 CREATE NONCLUSTERED INDEX，有 T-SQL 語法著色">
</p>

- **浮動結構預覽**：滑鼠停留在物件上查看摘要，或開啟浮動視窗檢視欄位、主外鍵與索引。切換到**指令碼**分頁可以直接捲動、選取並複製完整的 `CREATE TABLE` 或 DDL 定義。
- **F12 移至定義**：游標停在任何資料表、檢視或程序名稱上按 `F12`，直接在新查詢視窗開啟該物件的完整定義，並自動沿用當前連線與資料庫環境。

---

## 查詢結果格線工具

執行查詢後，在結果格線上選取資料並按右鍵，可以直接把格線上的資料轉成後續除錯或分享所需的內容，完全在本地記憶體處理，不發送額外查詢：

- **建立 #temp 指令碼**：把選取的資料轉成包含 `CREATE TABLE #SqlAssistRows`、批量 `INSERT` 與 `SELECT` 的完整腳本，在新查詢視窗直接執行。
- **複製成 IN 條件**：將選取的一至多欄複製為格式正確的 `IN ('val1', 'val2')` 述詞，直接貼進 `WHERE`。
- **複製成 Markdown 表格**：複製為對齊整齊的 Markdown 表格，方便直接貼到 PR、工單或通訊軟體討論。
- **複製成 JSON**：轉成一列一個物件的標準 JSON 陣列。
- **欄位剖析**：百欄寬表除錯利器，一次列出每一欄的 `NULL` 數、空字串數、唯一值數量與長度／數值範圍。
- **檢視儲存格完整內容**：突破 SSMS 格線 65,535 字元的截斷限制，以獨立視窗完整瀏覽與複製超長 XML、JSON 或文字。

---

## 程式碼片段與自動配對

- **45 組內建 T-SQL 片段**：包含常用 DDL、DML、條件判斷與管理語句，以 `Tab`／`Shift+Tab` 在預留參數間快速切換填值。
- **智慧符號配對**：輸入 `(`、`'`、`[` 自動補上對應符號，支援覆打跳過與成對 Backspace 刪除。

---

## 它能幫你做什麼？

| 寫 SQL 時遇到的事 | SqlAssist 的做法 |
|---|---|
| 記得用途，卻記不完整物件名稱 | 輸入 `libr`，用詞首感知的模糊比對找到 `Lib_Reader` |
| 不想離開編輯器查欄位與型別 | 輸入 `lr.`，直接列出該來源的欄位、型別與主索引鍵 |
| 想把 `SELECT *` 改成明確欄位 | 游標停在 `*` 後按 Tab，展開成逐行對齊的明確欄位清單 |
| 每次都要手動打 `INSERT` 或 `EXEC` 參數清單 | 選取物件後，自動補齊完整欄位清單、預設值骨架或具名參數 |
| 想改一個預存程序，卻要先去物件總管找 | 打 `ap` 選它，完整的 `ALTER` 定義直接載入編輯器就地修改 |
| 看到一個物件名稱，想知道它到底是怎麼寫的 | 游標停在名稱上按 F12，另開查詢視窗顯示可執行的定義，沿用目前連線 |
| 想先確認資料表或預存程序的結構與索引 | 滑鼠停留快速看摘要，或開啟浮動預覽直接複製完整的 DDL 指令碼 |
| 常寫重複的 SQL 樣板代碼 | 使用 45 組內建片段，並以 Tab／Shift+Tab 在欄位間移動填值 |
| 括號與引號老是漏掉右邊那一個 | 打 `(`、`'` 就補上另一半；打結尾字元跳過、Backspace 一起收掉 |
| 想拿查詢結果的幾列去本機 debug 重現 | 在結果格線上選取後按右鍵，一鍵產生 `#temp` 指令碼或 `IN` 條件 |
| 一百多欄的查詢結果不知道從哪看起 | 欄位剖析一次列出每一欄的 `NULL` 數、相異值數與長度範圍 |
| 單一儲存格塞了一大段 XML/JSON，格線上看不完 | 檢視完整內容，突破 65,535 字元限制，可以選、可以捲、可以複製 |
| 想把幾列查詢結果貼進工單或 PR 討論 | 複製成對齊好的 Markdown 表格 |

---

## 安裝

需要 **Windows x64** 與 **SSMS 22.9.x**。不必 clone 這個專案，也不必安裝 .NET SDK。

1. 到 [Releases](https://github.com/a73013110/SqlAssist.Ssms22/releases)，從最新版本的 **Assets** 下載 `SqlAssist.Ssms22.vsix`。
2. 儲存查詢，關閉所有 SSMS 視窗。
3. 開啟 `.vsix`，確認安裝目標是 **SQL Server Management Studio 22**。
4. 重新啟動 SSMS。看到 **工具 → SqlAssist** 就代表裝好了。

裝不起來、想知道第一次該試什麼、或要解除安裝，看[安裝與開始使用](docs/getting-started.md)。

> [!IMPORTANT]
> 請維持 SSMS 內建的 T-SQL IntelliSense 開啟。SqlAssist 預設只擋掉會互相干擾的內建自動建議清單，紅色錯誤波浪線、大綱與參數提示仍由 SSMS 提供。

> [!WARNING]
> [SSMS 目前未正式支援第三方擴充套件](https://learn.microsoft.com/en-us/ssms/faq#are-extensions-supported-in-ssms)；本專案以 SSMS 22.9.x 實機驗證，SSMS 更新後仍可能需要等待相容性確認。

---

## 使用與功能文件

| 我想知道…… | 文件 |
|---|---|
| 怎麼安裝、確認載入與開始使用 | [安裝與開始使用](docs/getting-started.md) |
| 建議清單、縮寫模糊比對與關鍵字大寫 | [建議清單](docs/completion.md) |
| INSERT / EXEC / ALTER 語句提交展開 | [提交時展開成整句](docs/completion-commit-expansion.md) |
| `SELECT *` 怎麼展開與排版 | [展開 SELECT *](docs/wildcard-expansion.md) |
| 內建／自訂片段與 Tab 欄位導航 | [程式碼片段](docs/snippets.md) |
| 括號與引號的自動配對 | [自動配對](docs/auto-pairing.md) |
| 滑鼠提示與完整物件結構預覽 | [物件結構預覽](docs/structure-preview.md) |
| F12 在新查詢視窗開啟物件定義 | [移至定義](docs/go-to-definition.md) |
| 把查詢結果變成 `#temp` 指令碼或 `IN` 條件 | [查詢結果格線](docs/result-grid.md) |
| 功能開關、顯示方式與診斷設定 | [設定](docs/settings.md) |

---

## 開發者入口

想閱讀原始碼、建置 VSIX 或參與開發，請從下列文件開始；README 只保留入口，不重複專案內部細節。

- [文件索引](docs/index.md)：依「想改什麼」或檔案路徑找到對應文件與程式碼。
- [建置、測試、安裝與發布](docs/development.md)：開發環境與工具腳本。
- [架構](docs/architecture.md)：Core、Metadata 與 SSMS 接線層的分工。
- [專案開發規範](CLAUDE.md)：動手前必讀的限制與踩坑記錄。

本專案採用 [MIT License](LICENSE)。
