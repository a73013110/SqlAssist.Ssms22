# 索引

先讀 [CLAUDE.md](../CLAUDE.md)，再依本頁選擇必要護欄與功能文件。
只讀與這次任務有關的章節；長文件先查標題／行號，不依連結把整棵文件樹讀完。

## 修改前必讀的護欄

按**實際修改的檔案與行為**選擇；跨領域讀取所有相關列，測試依被測功能比照。
本表指向的禁令與根目錄規則具有相同約束力，不能因為檔案搬家而略過。

| 會修改的範圍 | 必讀章節 |
|---|---|
| 任一層的 Settings、註冊 JSON、設定頁 | [設定](change-rules.md#設定) |
| `SqlAssist.Ssms22` 的接線、事件、命令、UI、MEF、連線與部署 | [平台接線](change-rules.md#平台接線) |
| Metadata 的查詢、快取、結構、可執行指令碼 | [中繼資料](change-rules.md#中繼資料) |
| Snippets、Parsing、Wildcards、補全上下文、SQL 掃描 | [片段與解析](change-rules.md#片段與解析) |
| 新增或調整跨功能共用邏輯 | 相關護欄＋[共用元件表](shared-components.md) |

## 我要改的是……

選功能文件後，再以標題或符號搜尋必要區段。仍不清楚程式位置時，查
[詳細路徑表](code-map.md#我要改的是)，不要先讀整份架構與全部來源。

| 任務或路徑關鍵字 | 文件 |
|---|---|
| 分層、命名空間、共用元件、平台邊界 | [架構](architecture.md) |
| 建議清單、Matching、排序、內建 IntelliSense | [補全](completion.md) |
| CompletionContext、Triggers、KeywordCase、出現時機 | [上下文與觸發](completion-context.md) |
| 別名欄位、Scope、ColumnSource、暫存表、重開清單 | [欄位解析](completion-columns.md) |
| 提交、插入文字、結構描述、方括號、INSERT／MERGE／EXEC／ALTER | [提交與展開](completion-commit-expansion.md) |
| Keywords、函式、型別、生成關鍵字 | [目錄](completion-catalogs.md) |
| 變數、全域變數、模組參數 | [變數補全](completion-variables.md) |
| Snippets、Tab Stop、片段管理、Tab／Enter | [片段](snippets.md) |
| Wildcards、SELECT *、Tab 展開 | [星號展開](wildcard-expansion.md) |
| Pairing、括號、引號 | [自動配對](auto-pairing.md) |
| QuickInfo、Preview、UI、Chrome、視窗與佈景 | [結構預覽](structure-preview.md) |
| F12、ShellCommandFilter、Definition、ScriptWindow | [移至定義](go-to-definition.md) |
| ResultGrid、結果轉 SQL | [結果格線](result-grid.md) |
| Settings、Monikers、registration、Limits | [設定](settings.md) |
| Metadata、Querying、Caching、Formatting、Model | [中繼資料](metadata.md) |
| 跨資料庫、四段式名稱、連結伺服器 | [中繼資料](metadata.md)＋[上下文與觸發](completion-context.md) |
| 建置、測試、tools、版本、安裝、部署、診斷 | [開發](development.md) |
| AI 入門、新設備、PS 腳本用途、按需讀取與輸出節流 | [AI 工作流程：先看這頁](ai-workflow.md) |
| 安裝／設定／使用／驗證 RTK | [RTK 教學](ai-rtk.md) |
| Serena：本機 CLI、Claude／Codex MCP、符號搜尋 | [Serena 教學](ai-serena.md) |

## 資料夾對應

細表在 [程式碼地圖](code-map.md#資料夾對應)。`tests/` 鏡像被測專案的資料夾；
Core／Metadata 可單元測試，Ssms22 只接平台，不另放可獨立測試的商業邏輯。

## 只有一份的東西

需要新增共用邏輯時才查 [唯一實作](shared-components.md)，不要複製新版本。

## 新增設定

先讀上方設定護欄；四個必改位置與守門測試在 [新增設定](code-map.md#新增設定)。

## 工具腳本

腳本清單在 [開發文件](development.md#工具腳本)；完整紀錄與輸出上限在
[AI 共用工作流程](ai-workflow.md#工具輸出節流)。

## 測試

路徑與游標測試助手見 [測試對照](code-map.md#測試)，執行方式見
[建置與測試](development.md#建置與測試)。節流不改變測試範圍與判定。

## 我只想安裝或使用

先看 [安裝與開始使用](getting-started.md)。功能說明直接使用上方對應文件，不需要讀代理護欄。
