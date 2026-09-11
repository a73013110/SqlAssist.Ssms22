# 文件路由

只讀命中的護欄與主題；護欄必讀，非延伸閱讀。

## 修改前護欄

| 實際變更 | 必讀 |
|---|---|
| `.cs`、測試、專案檔、`tools/` | [程式碼](rules-code.md) |
| `CLAUDE.md`、`AGENTS.md`、README、`docs/`、AI 工具 | [文件](rules-docs.md) |
| Settings、registration、設定頁 | [設定](rules-settings.md) |
| Ssms22 事件、命令、MEF、連線、部署 | [平台](rules-platform.md) |
| 自製視窗、控制項、排版、色彩 | [平台](rules-platform.md)＋[UI 準則](ui-guidelines.md) |
| Metadata 查詢、快取、結構、指令碼、健檢 | [中繼資料](rules-metadata.md) |
| Snippets、Parsing、Wildcards、上下文、SQL 掃描 | [片段與解析](rules-parsing.md) |
| 跨功能共用邏輯 | 上述護欄＋[唯一實作](shared-components.md)／[平台](shared-components-platform.md) |

## 主題

| 關鍵字／症狀 | 文件 |
|---|---|
| 分層、平台邊界、原生補全管線、SqlAssistPlatformGuard、Run／Probe／Begin | [架構](architecture.md)／[平台 Guard](platform-guard.md) |
| 不知道該改哪個型別／資料夾 | [症狀→程式碼](code-map.md)／[資料夾](folder-map.md) |
| 建議清單、Matching、排名、IntelliSense | [補全](completion.md) |
| CompletionContext、Triggers、KeywordCase、一般位置 | [上下文](completion-context.md) |
| `COLLATE` 之後、定序名單、fn_helpcollations | [定序](completion-collation.md) |
| `ON` 是資料表或述詞、MERGE 動作子句 | [ON／MERGE](completion-on-merge.md) |
| TVF／純量函式、系統物件範圍 | [物件種類](completion-object-kinds.md) |
| 多段式名稱、資料庫／結構描述判定 | [限定名稱](qualified-names.md) |
| 別名欄位、ColumnSource、暫存表、資料表變數 | [欄位](completion-columns.md)／[指令碼宣告](script-tables.md) |
| Scope、括號範圍、重開清單 | [範圍與重開](completion-reopen.md) |
| 提交名稱、結構描述、方括號、點號 | [插入文字](completion-insertion.md) |
| ALTER／INSERT／MERGE／EXEC 展開、游標、復原 | [整句展開](statement-expansion.md) |
| INSERT 欄位、EXEC 參數、預留值 | [展開內容](statement-values.md) |
| 自訂函式括號、引數預留值、參數資訊 | [函式呼叫](function-call-insertion.md) |
| 關鍵字產生器、位置旗標、物件過濾 | [關鍵字](completion-keywords.md) |
| 子句回溯、別名換行邊界、數值不開清單 | [關鍵字邊界](completion-keyword-context.md)／[不開清單](completion-no-list.md) |
| 內建函式、資料型別目錄 | [函式與型別](completion-builtins.md) |
| 內建名稱的用途、範例、style、datepart | [內建說明](builtin-help.md)／[辨識](builtin-help-recognition.md) |
| 函式簽章、目前第幾個引數 | [參數提示](parameter-hint.md) |
| 變數、全域變數、模組參數 | [變數](completion-variables.md) |
| 片段內容與接續建議 | [片段](snippets.md) |
| Tab Stop、欄位建議、Tab／Enter | [片段導航](snippet-navigation.md)／[Tab Stop 建議](snippet-field-list.md) |
| 包住選取範圍、`$surround$` | [片段包夾](snippet-surround.md)／[按鍵](snippet-surround-keys.md) |
| 使用者 override、合併、存檔、手改違規 | [片段存放](snippet-storage.md) |
| SELECT *、Wildcards、Tab 展開 | [星號展開](wildcard-expansion.md) |
| Pairing、括號、引號 | [自動配對](auto-pairing.md) |
| BEGIN／END、CASE、高亮、BlockMatcher | [區塊配對](block-matching.md)／[配色](block-colors.md)／[驗收](block-matching-validation.md) |
| QuickInfo、物件預覽、暫存表／變數／CTE | [結構預覽](structure-preview.md)／[指令碼宣告](script-declared-objects.md) |
| Popup、Placement、方向、焦點、按需載入、Resize、效能 | [預覽視窗](preview-window.md)／[預覽互動](preview-interaction.md) |
| Chrome、視覺規格、對話框排版、深淺主題切換、配色快取、分類色、高對比 | [UI 準則](ui-guidelines.md)／[主題連動](themes.md) |
| 背景載入、計數、合併、統計、退場、玻璃提示 | [通知提示](notifications.md)／[呈現與驗證](notifications-ui.md) |
| 三軸、可見度、降級、種類開關、改名、文案 | [可見度](notifications-visibility.md)／[設計](notifications-design.md)／[訊息](notifications-messages.md) |
| 指令碼風格、選項、還原度、資料不齊時註解、健檢規則、嚴重度、誤報 | [指令碼產生](script-generation.md)／[結構健檢](schema-analysis.md) |
| F12 物件種類、產生定義、失敗註解、執行緒、新查詢視窗 | [F12 指令碼](definition-scripts.md)／[移至定義](go-to-definition.md) |
| ShellCommandFilter、命令表、鍵繫結 | [殼層命令](shell-commands.md) |
| ResultGrid 命令、JSON、欄位剖析、字面值、精確度、輸出效能 | [結果格線](result-grid.md)／[格線輸出](result-grid-generation.md) |
| 設定項、非設定項、按鈕、選單、關於、enableWhen、enum 相容 | [設定](settings.md)／[入口](settings-entries.md)／[設定結構](settings-schema.md) |
| 分層載入、跨資料庫 | [中繼資料](metadata.md)／[跨資料庫](metadata-cross-db.md) |
| 目前資料庫、USE、連結伺服器、OPENQUERY、遠端失敗、舊版 SQL、權限、缺欄位、降級 | [目前連線](metadata-connection.md)／[遠端中繼資料](metadata-remote.md)／[相容與失敗](metadata-compatibility.md) |
| 建置、測試、UTF-8／LF、BOM、輸出編碼 | [開發](development.md)／[文字與編碼](text-encoding.md) |
| 組件版本不符、FileNotFoundException | [程式碼](rules-code.md) |
| 版本、發布、安裝、解除安裝、VSIX 偵錯、MEF／命令快取、診斷 | [發布](release.md)／[偵錯](debugging.md) |
| AI 分段讀取、輸出節流、RTK、README 截圖、logo、social preview | [AI 工作流程](ai-workflow.md)／[RTK](ai-rtk.md)／[圖片規則](images/README.md)／[提示詞](images/prompts.md) |

只使用產品時讀[開始使用](getting-started.md)。
