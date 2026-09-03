# 詳細程式碼路徑表

範圍：已看過 [索引](index.md)，但還不知道該進哪個型別時才查本表。
先依索引讀取護欄，不要把本表整份當作每個 task 的必要開場。
共用實作另見 [共用元件表](shared-components.md)，工具清單見 [開發文件](development.md#工具腳本)。

## 我要改的是……

以下開始是開發者的部分。動手之前先讀 [CLAUDE.md](../CLAUDE.md)——那是這個專案踩過坑
之後定下來的硬規則，每一條後面都有一次實際的事故；再讀下表對應的那一份文件。

| 想做的事 | 先讀 | 從這裡進去 |
|---|---|---|
| 建議清單多／少了某一類項目 | [completion.md](completion.md) | `Core/Completion/BuiltInSuggestionCatalog.cs`、`Ssms22/Completion/SqlAsyncCompletionSource.cs` |
| 排名順序不對 | [completion.md](completion.md) | `Core/Matching/FuzzyMatcher.cs`、`Core/Completion/SuggestionMatcher.cs` |
| 某個位置不該開清單／該開沒開 | [completion-context.md](completion-context.md) | `Core/Completion/SqlCompletionContextAnalyzer.cs`、`Core/Completion/SqlCompletionTriggers.cs` |
| 打完某個字沒有重開清單 | [completion-columns.md](completion-columns.md) | `Ssms22/Completion/SqlCompletionReopen.cs` |
| SSMS 自己的清單也跟著彈出來 | [completion.md](completion.md) | `Ssms22/Settings/NativeMemberList.cs`（**不要**去關內建 IntelliSense 的總開關） |
| 提交建議後寫進去的文字不對 | [completion-commit-expansion.md](completion-commit-expansion.md) | `Ssms22/Completion/SqlInsertionText.cs`、`SqlAsyncCompletionCommitManager.cs` |
| `INSERT INTO`／`MERGE INTO`／`EXEC`／`ALTER` 提交後展開的整句不對 | [completion-commit-expansion.md](completion-commit-expansion.md) | `Core/Statements/`（排版與規則）、`Ssms22/Completion/SqlCommitExpansions.cs` |
| 展開的整句蓋錯位置或沒有蓋上去 | [completion-commit-expansion.md](completion-commit-expansion.md) | `Ssms22/Completion/SqlCommitExpander.cs` |
| 關鍵字清單要增刪 | [completion-catalogs.md](completion-catalogs.md) | `tools/Generate-Keywords.ps1`（**不要**手改 `.Generated.cs`） |
| 內建函式、全域變數或型別要增刪 | [completion-catalogs.md](completion-catalogs.md)、[completion-variables.md](completion-variables.md) | `Core/Keywords/` 底下的 `SqlFunctionCatalog.cs`、`SqlGlobalVariableCatalog.cs`、`SqlDataTypeCatalog.cs` |
| 自動大寫的時機 | [completion-context.md](completion-context.md) | `Core/Keywords/SqlKeywordCase.cs`、`Ssms22/Editor/SqlKeywordCasing.cs` |
| 括號或引號補得不是時候、跳不過去 | [auto-pairing.md](auto-pairing.md) | `Core/Pairing/SqlAutoPairAnalyzer.cs`、`Ssms22/Editor/SqlAutoPairing.cs` |
| `@` 或 `@@` 之後列出來的東西不對 | [completion-variables.md](completion-variables.md) | `Core/Completion/SqlScriptVariableSuggestions.cs`、`SqlExecutedModule.cs`、`Core/Keywords/SqlGlobalVariableCatalog.cs` |
| `別名.` 列出來的欄位不對 | [completion-columns.md](completion-columns.md) | `Core/Parsing/SqlScopeAnalyzer.cs`、`Core/Parsing/SqlColumnSourceResolver.cs` |
| `#tmp`／`@rows` 的欄位列不出來或展不開 | [completion-columns.md](completion-columns.md) | `Core/Parsing/SqlScriptTableCollector.cs` |
| 程式碼片段的格式、合併或展開行為 | [snippets.md](snippets.md) | `Core/Snippets/DefaultSnippets.json`、`SqlSnippetExpansion.cs`、`SqlSnippetMerger.cs`、`SqlSnippetSerializer.cs` |
| `SELECT *` 展不開或展錯 | [wildcard-expansion.md](wildcard-expansion.md) | `Core/Wildcards/SqlWildcardAnalyzer.cs` |
| 展開後的欄位排版 | [wildcard-expansion.md](wildcard-expansion.md) | `Core/Wildcards/SqlWildcardExpansionText.cs` |
| Tab／Shift+Tab 的行為 | [snippets.md](snippets.md)、[wildcard-expansion.md](wildcard-expansion.md) | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 滑鼠停留提示的內容 | [structure-preview.md](structure-preview.md) | `Ssms22/QuickInfo/SqlQuickInfoContentBuilder.cs` |
| 浮動預覽的行為或擺放 | [structure-preview.md](structure-preview.md) | `Ssms22/Preview/SqlStructurePreview.cs` |
| 任何顏色、字型、按鈕樣式 | [structure-preview.md](structure-preview.md) | `Ssms22/UI/SqlAssistChrome.cs`（**唯一**出處） |
| 按了某個鍵卻沒反應（F12 之類） | [go-to-definition.md](go-to-definition.md) | `Ssms22/Editor/SqlShellCommandFilter.cs`（**唯一**攔得到殼層命令的位置） |
| F12 抵達了卻沒開視窗 | [go-to-definition.md](go-to-definition.md) | `Ssms22/Editor/SqlDefinitionOpener.cs` |
| 結果格線右鍵選單的命令、產出的 SQL 不對 | [result-grid.md](result-grid.md) | `Metadata/ResultGrid/`、`Ssms22/ResultGrid/` |
| 新增選單項目或鍵繫結後沒生效 | [development.md](development.md) | `Menus.vsct` ＋ `ProvideMenuResource` 版號，且必須重新安裝 |
| F12 開出來的指令碼內容不對 | [go-to-definition.md](go-to-definition.md) | `Metadata/Formatting/SqlObjectScript.cs` |
| 新查詢視窗沒有沿用連線 | [go-to-definition.md](go-to-definition.md) | `Ssms22/Connections/SsmsScriptWindow.cs` |
| 新增一個設定 | [settings.md](settings.md) | 四處都要動，見下方「新增設定」 |
| 查詢的 SQL 或載入分層 | [metadata.md](metadata.md) | `Metadata/Querying/SqlMetadataQueries.cs` |
| 連不上資料庫時的行為 | [metadata.md](metadata.md) | `Metadata/Caching/SqlMetadataCatalog.cs` |
| 指令碼整段變成註解（缺定義、缺欄位） | [metadata.md](metadata.md) | `Metadata/Model/SqlObjectStructure.cs` 的 `CanBuildExecutableScript` |
| 建置、安裝、偵錯、發布 | [development.md](development.md) | `tools/` 底下的腳本，見開發文件的工具表 |
| 分層規則、資料夾規則 | [architecture.md](architecture.md) | — |

## 資料夾對應

`src/X/Foo/` 一律是命名空間 `X.Foo`，`tests/` 鏡像同一份路徑。

### SqlAssist.Core（netstandard2.0，零 VS 相依，可完整單元測試）

| 資料夾 | 職責 | 文件 |
|---|---|---|
| `Completion/` | 建議項的模型、上下文判斷、篩選與排名 | [completion.md](completion.md)、[completion-context.md](completion-context.md) |
| `Keywords/` | 關鍵字、內建函式、全域變數與型別目錄、位置分層、自動大寫 | [completion-catalogs.md](completion-catalogs.md)、[completion-context.md](completion-context.md) |
| `Matching/` | 與領域無關的字串模糊比對（**禁止**參照 `Completion/`） | [completion.md](completion.md) |
| `Pairing/` | 輸入分隔字元時要不要補上另一半 | [auto-pairing.md](auto-pairing.md) |
| `Parsing/` | 詞法分析、註解與括號、範圍與欄位來源解析 | [completion-columns.md](completion-columns.md)、[architecture.md](architecture.md) |
| `Preview/` | 浮動預覽的矩形定位、避障、方向遲滯與雙側縮放 | [structure-preview.md](structure-preview.md) |
| `Snippets/` | 程式碼片段的模型、展開、佔位符與序列化 | [snippets.md](snippets.md) |
| `Statements/` | 提交後展開成整句的排版與規則（`INSERT` 骨架、`MERGE` 骨架、`EXEC` 呼叫、參數預設值） | [completion-commit-expansion.md](completion-commit-expansion.md) |
| `Wildcards/` | `SELECT *` 的判斷與展開後的排版 | [wildcard-expansion.md](wildcard-expansion.md) |
| `Settings/` | 設定 POCO、moniker、數值範圍與讀取 | [settings.md](settings.md) |
| `Diagnostics/` | 版本解讀、健康檢查，以及視窗與匿名診斷摘要共用的欄位清單 | [development.md](development.md) |
| `Json/` | 最小 JSON 讀寫（Snippet 檔與註冊檔測試用） | — |

### SqlAssist.Metadata（netstandard2.0，只依賴 `System.Data`）

| 資料夾 | 職責 | 文件 |
|---|---|---|
| `Model/` | 物件、欄位、參數、索引、外來鍵的模型 | [metadata.md](metadata.md) |
| `Querying/` | 分層的中繼資料查詢與資料列對應 | [metadata.md](metadata.md) |
| `Caching/` | 依「伺服器＋資料庫」快取，並協調分層載入 | [metadata.md](metadata.md) |
| `Formatting/` | 型別字串、識別字括號、欄位的呈現語意，以及可執行指令碼的批次樣板 | [metadata.md](metadata.md)、[go-to-definition.md](go-to-definition.md) |
| `ResultGrid/` | 查詢結果的欄位與資料列模型、值轉字面值、`#temp` 與 `IN` 的產生 | [result-grid.md](result-grid.md) |

### SqlAssist.Ssms22（net48 VSIX，只做接線）

| 資料夾 | 職責 | 文件 |
|---|---|---|
| `Completion/` | 平台原生非同步 IntelliSense 的來源、排序、提交與重開 | [completion.md](completion.md)、[completion-commit-expansion.md](completion-commit-expansion.md) |
| `Editor/` | 編輯器接線、Tab 與 Enter 的優先順序、非同步寫回、物件定位、大寫改寫、分隔字元配對、F12 移至定義 | [architecture.md](architecture.md)、[go-to-definition.md](go-to-definition.md) |
| `QuickInfo/` | 滑鼠停留提示 | [structure-preview.md](structure-preview.md) |
| `Preview/` | 浮動結構預覽與其專屬外觀 | [structure-preview.md](structure-preview.md) |
| `Wildcards/` | `SELECT *` 的展開與可展開提示（Tab 由 `Editor/` 分派） | [wildcard-expansion.md](wildcard-expansion.md) |
| `Snippets/` | 片段檔、管理員視窗與原生 Expansion Session | [snippets.md](snippets.md) |
| `Settings/` | Unified Settings 讀取、預覽視窗尺寸，以及推給 SSMS 的語言偏好 | [settings.md](settings.md) |
| `Connections/` | 取得 SSMS 查詢視窗的連線，以及另開一個沿用連線的查詢視窗 | [metadata.md](metadata.md)、[go-to-definition.md](go-to-definition.md) |
| `Commands/` | 命令識別碼、工具選單命令與「關於與診斷」視窗 | [development.md](development.md) |
| `ResultGrid/` | 從 SSMS 結果格線取出選取範圍，並把產出交給新查詢視窗或剪貼簿 | [result-grid.md](result-grid.md) |
| `UI/` | 全擴充共用的外觀與佈景筆刷 | [structure-preview.md](structure-preview.md) |

## 新增設定

四處必改的位置、設定限制與守門測試見 [設定](settings.md#新增一個設定)。

## 測試

`tests/` 鏡像 `src/` 的資料夾結構，所以改了 `Core/Parsing/` 就去看
`tests/SqlAssist.Core.Tests/Parsing/`。只有兩個測試專案：`SqlAssist.Core.Tests`
與 `SqlAssist.Metadata.Tests`——`SqlAssist.Ssms22` 沒有測試專案，這正是
「**禁止**把只看文字就能判斷的邏輯寫進 Ssms22」的原因。

游標位置的測試寫法見 `tests/SqlAssist.Core.Tests/SqlWithCaret.cs`。
