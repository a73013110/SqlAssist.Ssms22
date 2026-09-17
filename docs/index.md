# 文件路由

## 修改前護欄

| 實際變更 | 必讀 |
|---|---|
|`.cs`、測試、專案檔、`tools/`、組件版本不符|[程式碼](rules-code.md)|
|`CLAUDE.md`、`AGENTS.md`、README、`docs/`、AI 工具|[文件](rules-docs.md)|
|Settings、registration、設定頁|[設定](rules-settings.md)|
|Ssms22 事件、命令、MEF、連線、部署|[平台](rules-platform.md)|
|自製 UI|[平台](rules-platform.md)＋[UI 準則](ui-guidelines.md)|
|Metadata|[中繼資料](rules-metadata.md)|
|Snippets、Parsing、Wildcards、上下文、SQL 掃描|[片段與解析](rules-parsing.md)|
|跨功能共用邏輯|上述護欄＋[唯一實作](shared-components.md)／[平台](shared-components-platform.md)|

## 主題

| 關鍵字／症狀 | 文件 |
|---|---|
| 分層、平台邊界、Guard | [架構](architecture.md)／[平台 Guard](platform-guard.md) |
| 型別／資料夾 | [症狀→程式碼](code-map.md)／[資料夾](folder-map.md) |
| SQL Memory／History／Favorites | [產品與架構](sql-memory.md)／[儲存與收藏](sql-memory-storage.md)／[搜尋](sql-memory-search.md)／[UI](sql-memory-ui.md) |
| 收藏版本歷史、回溯、SQL 差異 | [版本歷史](sql-memory-revisions.md) |
| SQL Memory 保留、用量、清理、部署 | [維護](sql-memory-maintenance.md)／[用量](sql-memory-usage.md)／[驗收](sql-memory-validation.md) |
| 建議清單、排名、IntelliSense | [補全](completion.md) |
| CompletionContext、觸發、大小寫 | [上下文](completion-context.md) |
| `COLLATE` 之後、定序名單、fn_helpcollations | [定序](completion-collation.md) |
| `ON` 是資料表或述詞、MERGE 動作子句 | [ON／MERGE](completion-on-merge.md) |
| TVF／純量函式、系統物件範圍 | [物件種類](completion-object-kinds.md) |
| 多段式名稱、資料庫／結構描述判定 | [限定名稱](qualified-names.md) |
| 別名欄位、ColumnSource、暫存表 | [欄位](completion-columns.md)／[指令碼宣告](script-tables.md) |
| Scope、括號、重開 | [範圍與重開](completion-reopen.md) |
| 提交名稱、結構描述、方括號 | [插入文字](completion-insertion.md) |
| 整句展開、游標、復原 | [整句展開](statement-expansion.md) |
| INSERT 欄位、EXEC 參數、預留值 | [展開內容](statement-values.md) |
| 自訂函式括號、引數預留值 | [函式呼叫](function-call-insertion.md) |
| 關鍵字產生器、位置旗標、物件過濾 | [關鍵字](completion-keywords.md) |
| 子句回溯、換行邊界、不開清單 | [關鍵字邊界](completion-keyword-context.md)／[不開清單](completion-no-list.md) |
| 內建函式、資料型別目錄 | [函式與型別](completion-builtins.md) |
| 用途、範例、style、datepart | [內建說明](builtin-help.md)／[辨識](builtin-help-recognition.md) |
| 函式簽章、目前第幾個引數 | [參數提示](parameter-hint.md) |
| 變數、全域變數、模組參數 | [變數](completion-variables.md) |
| 片段內容與接續建議 | [片段](snippets.md) |
| Tab Stop、欄位、Tab／Enter | [片段導航](snippet-navigation.md)／[欄位建議](snippet-field-list.md) |
| 包住選取範圍、`$surround$` | [片段包夾](snippet-surround.md)／[按鍵](snippet-surround-keys.md) |
| 使用者 override、合併、存檔 | [片段存放](snippet-storage.md) |
| SELECT *、Wildcards、Tab 展開 | [星號展開](wildcard-expansion.md) |
| Pairing、括號、引號 | [自動配對](auto-pairing.md) |
| BEGIN／END、CASE、高亮、BlockMatcher | [區塊配對](block-matching.md)／[配色](block-colors.md)／[驗收](block-matching-validation.md) |
| QuickInfo、預覽、暫存表／變數／CTE | [結構預覽](structure-preview.md)／[宣告](script-declared-objects.md) |
| 預覽視窗、定位、焦點、Resize | [預覽視窗](preview-window.md)／[預覽互動](preview-interaction.md) |
| Chrome、配色、高對比、對話框頁尾 | [UI 準則](ui-guidelines.md)／[主題連動](themes.md)／[對話框](ui-dialogs.md) |
| 通知生命週期與呈現 | [通知提示](notifications.md)／[呈現與驗證](notifications-ui.md) |
| 通知可見度、分級、文案 | [可見度](notifications-visibility.md)／[設計](notifications-design.md)／[訊息](notifications-messages.md) |
| 指令碼風格、降級註解、健檢 | [指令碼產生](script-generation.md)／[結構健檢](schema-analysis.md) |
| F12、定義、失敗註解、新查詢 | [F12 指令碼](definition-scripts.md)／[移至定義](go-to-definition.md) |
| ShellCommandFilter、命令表、鍵繫結 | [殼層命令](shell-commands.md) |
| ResultGrid 命令、JSON、欄位剖析、字面值、輸出效能 | [結果格線](result-grid.md)／[格線輸出](result-grid-generation.md) |
| 設定、入口、enableWhen、enum 相容 | [設定](settings.md)／[入口](settings-entries.md)／[設定結構](settings-schema.md) |
| 分層載入、跨資料庫 | [中繼資料](metadata.md)／[跨資料庫](metadata-cross-db.md) |
| USE、連結伺服器、OPENQUERY、舊版 SQL、權限 | [連線](metadata-connection.md)／[遠端](metadata-remote.md)／[相容](metadata-compatibility.md) |
| 建置／測試、UTF-8／LF／BOM | [開發](development.md)／[文字與編碼](text-encoding.md) |
| 發布、安裝／移除、VSIX 偵錯、MEF 快取 | [發布](release.md)／[偵錯](debugging.md) |
| Debug 部署、必要／可選檔案、Deploy／Install | [部署契約](debug-deployment.md) |
| AI／RTK、README 圖片 | [AI](ai-workflow.md)／[RTK](ai-rtk.md)／[圖片](images/README.md)／[提示詞](images/prompts.md) |

只使用產品時讀[開始使用](getting-started.md)。
