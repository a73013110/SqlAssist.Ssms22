# 文件路由

只讀本次修改命中的護欄與主題；跨範圍取聯集。先以檔名、符號或標題定位，不沿連結
預讀整棵文件樹。護欄是必讀規則，不是延伸閱讀。

## 修改前護欄

| 實際變更 | 必讀 |
|---|---|
| `.cs`、測試、專案檔、`tools/` | [程式碼](rules-code.md) |
| `CLAUDE.md`、`AGENTS.md`、README、`docs/`、AI 工具 | [文件](rules-docs.md) |
| Settings、registration、設定頁 | [設定](rules-settings.md) |
| Ssms22 事件、命令、MEF、連線、部署 | [平台](rules-platform.md) |
| 自製視窗、控制項、排版、色彩 | [平台](rules-platform.md)＋[UI 準則](ui-guidelines.md) |
| Metadata 查詢、快取、結構、指令碼 | [中繼資料](rules-metadata.md) |
| Snippets、Parsing、Wildcards、上下文、SQL 掃描 | [片段與解析](rules-parsing.md) |
| 跨功能共用邏輯 | 上述護欄＋[唯一實作](shared-components.md) |

## 主題

| 關鍵字／症狀 | 文件 |
|---|---|
| 分層、平台邊界、原生補全管線 | [架構](architecture.md) |
| SqlAssistPlatformGuard、Run／Probe／Begin | [平台 Guard](platform-guard.md) |
| 不知道該改哪個型別／資料夾 | [症狀→程式碼](code-map.md)／[資料夾](folder-map.md) |
| 建議清單、Matching、排名、內建 IntelliSense | [補全](completion.md) |
| CompletionContext、Triggers、KeywordCase、一般位置 | [上下文](completion-context.md) |
| `ON` 是資料表或述詞、MERGE 動作子句 | [ON／MERGE](completion-on-merge.md) |
| TVF／純量函式、系統物件出現範圍 | [物件種類](completion-object-kinds.md) |
| 多段式名稱、資料庫／結構描述判定、右對齊 | [限定名稱](qualified-names.md) |
| 別名欄位、ColumnSource、暫存表、資料表變數 | [欄位](completion-columns.md) |
| Scope、括號範圍、清單重開 | [範圍與重開](completion-reopen.md) |
| 提交名稱、結構描述、方括號、點號 | [插入文字](completion-insertion.md) |
| ALTER／INSERT／MERGE／EXEC 展開、游標、復原 | [整句展開](statement-expansion.md) |
| 函式引數、INSERT 欄位、EXEC 參數、預留值 | [展開內容](statement-values.md) |
| 關鍵字產生器、位置旗標、物件過濾 | [關鍵字](completion-keywords.md) |
| 子句回溯、別名、換行邊界、數值不開清單 | [關鍵字邊界](completion-keyword-context.md) |
| 內建函式、資料型別目錄 | [函式與型別](completion-builtins.md) |
| 變數、全域變數、模組參數 | [變數](completion-variables.md) |
| 片段內容與接續建議 | [片段](snippets.md) |
| Tab Stop、欄位建議、Tab／Enter | [片段導航](snippet-navigation.md) |
| 使用者 override、合併、遷移、存檔 | [片段存放](snippet-storage.md) |
| SELECT *、Wildcards、Tab 展開 | [星號展開](wildcard-expansion.md) |
| Pairing、括號、引號 | [自動配對](auto-pairing.md) |
| QuickInfo、物件預覽內容 | [結構預覽](structure-preview.md) |
| Popup、Placement、方向、焦點 | [預覽視窗](preview-window.md) |
| 預覽操作、按需載入、Resize、效能 | [預覽互動](preview-interaction.md) |
| Chrome、視覺規格、對話框排版 | [UI 準則](ui-guidelines.md) |
| F12 物件種類、產生定義、失敗註解 | [F12 指令碼](definition-scripts.md) |
| F12 執行緒、連線、新查詢視窗 | [移至定義](go-to-definition.md) |
| ShellCommandFilter、命令表、鍵繫結 | [殼層命令](shell-commands.md) |
| ResultGrid 命令、JSON、欄位剖析、完整內容 | [結果格線](result-grid.md) |
| 字面值、長度、精確度、輸出效能 | [格線輸出](result-grid-generation.md) |
| 設定項、分類、按鈕、非設定項 | [設定](settings.md) |
| 新增設定、enableWhen、enum 相容 | [設定結構](settings-schema.md) |
| 中繼資料分層載入、跨資料庫 | [中繼資料](metadata.md) |
| 連結伺服器、OPENQUERY、遠端失敗 | [遠端中繼資料](metadata-remote.md) |
| 舊版 SQL、權限、缺欄位、降級 | [相容與失敗](metadata-compatibility.md) |
| 建置、測試、文字格式、工具腳本 | [開發](development.md) |
| 版本、發布、安裝、解除安裝 | [發布](release.md) |
| VSIX 偵錯、MEF／命令快取、診斷 | [偵錯](debugging.md) |
| AI 分段讀取、輸出節流、新設備 | [AI 工作流程](ai-workflow.md) |
| RTK 安裝與限制 | [RTK](ai-rtk.md) |
| README 截圖、logo、social preview、生成圖 | [圖片規則](images/README.md)／[提示詞](images/prompts.md) |

只安裝或使用產品時讀[開始使用](getting-started.md)，不需要代理護欄。
