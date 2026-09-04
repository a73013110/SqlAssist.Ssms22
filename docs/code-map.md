# 詳細程式碼路徑表

範圍：已看過 [索引](index.md)，但還不知道該進哪個型別時才查本表。
索引已負責文件路由，本表不重複文件欄；共用實作另見[共用元件表](shared-components.md)。

## 我要改的是……

| 想做的事 | 從這裡進去 |
| --- | --- |
| 建議清單多／少了某一類項目 | `Core/Completion/BuiltInSuggestionCatalog.cs`、`Ssms22/Completion/SqlAsyncCompletionSource.cs` |
| 排名順序不對 | `Core/Matching/FuzzyMatcher.cs`、`Core/Completion/SuggestionMatcher.cs` |
| 某個位置不該開清單／該開沒開 | `Core/Completion/SqlCompletionContextAnalyzer.cs`、`Core/Completion/SqlCompletionTriggers.cs` |
| 打完某個字沒有重開清單 | `Ssms22/Completion/SqlCompletionReopen.cs` |
| SSMS 自己的清單也跟著彈出來 | `Ssms22/Settings/NativeMemberList.cs`（**不要**去關內建 IntelliSense 的總開關） |
| 提交建議後寫進去的文字不對 | `Core/Completion/SqlInsertionText.cs`（規則）、`Ssms22/Completion/SqlAsyncCompletionCommitManager.cs`（接線） |
| `INSERT INTO`／`MERGE INTO`／`EXEC`／`ALTER` 展開內容不對 | `Core/Statements/`、`Ssms22/Completion/SqlCommitExpansions.cs` |
| 展開的整句蓋錯位置或沒有蓋上去 | `Ssms22/Completion/SqlCommitExpander.cs` |
| 關鍵字清單要增刪 | `tools/Generate-Keywords.ps1`（**不要**手改 `.Generated.cs`） |
| 內建函式、全域變數或型別要增刪 | `Core/Keywords/` 底下的三個 Catalog |
| 自動大寫的時機 | `Core/Keywords/SqlKeywordCase.cs`、`Ssms22/Editor/SqlKeywordCasing.cs` |
| 括號或引號補得不是時候、跳不過去 | `Core/Pairing/SqlAutoPairAnalyzer.cs`、`Ssms22/Editor/SqlAutoPairing.cs` |
| `@` 或 `@@` 之後列出來的東西不對 | `Core/Completion/SqlScriptVariableSuggestions.cs`、`SqlExecutedModule.cs`、`Core/Keywords/SqlGlobalVariableCatalog.cs` |
| `別名.` 列出來的欄位不對 | `Core/Parsing/SqlScopeAnalyzer.cs`、`Core/Parsing/SqlColumnSourceResolver.cs` |
| `#tmp`／`@rows` 的欄位列不出來或展不開 | `Core/Parsing/SqlScriptTableCollector.cs` |
| 程式碼片段的格式或展開行為 | `Core/Snippets/DefaultSnippets.json`、`SqlSnippetExpansion.cs` |
| 片段合併、override 或存檔 | `SqlSnippetMerger.cs`、`SqlSnippetSerializer.cs` |
| `SELECT *` 展不開或展錯 | `Core/Wildcards/SqlWildcardAnalyzer.cs` |
| 展開後的欄位排版 | `Core/Wildcards/SqlWildcardExpansionText.cs` |
| Tab／Shift+Tab 的行為 | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 滑鼠停留提示的內容 | `Ssms22/QuickInfo/SqlQuickInfoContentBuilder.cs` |
| 浮動預覽的行為或擺放 | `Ssms22/Preview/SqlStructurePreview.cs` |
| 任何自製 UI、顏色、字型或排版 | `Ssms22/UI/SqlAssistChrome.cs`（**唯一**出處） |
| 按了某個鍵卻沒反應（F12 之類） | `Ssms22/Editor/SqlShellCommandFilter.cs` |
| F12 抵達了卻沒開視窗 | `Ssms22/Editor/SqlDefinitionOpener.cs` |
| 結果格線右鍵選單的命令、產出的 SQL 不對 | `Metadata/ResultGrid/`、`Ssms22/ResultGrid/` |
| 新增選單項目或鍵繫結後沒生效 | `Menus.vsct` ＋ `ProvideMenuResource` 版號，且必須重新安裝 |
| F12 開出來的指令碼內容不對 | `Metadata/Formatting/SqlObjectScript.cs` |
| 新查詢視窗沒有沿用連線 | `Ssms22/Connections/SsmsScriptWindow.cs` |
| 新增一個設定 | 註冊 JSON、POCO、moniker、reader 四處 |
| 查詢的 SQL 或載入分層 | `Metadata/Querying/SqlMetadataQueries.cs` |
| 連不上資料庫時的行為 | `Metadata/Caching/SqlMetadataCatalog.cs` |
| 指令碼整段變成註解（缺定義、缺欄位） | `Metadata/Model/SqlObjectStructure.cs` 的 `CanBuildExecutableScript` |
| 建置、安裝、偵錯、發布 | `tools/` |
| 分層規則、資料夾規則 | — |

## 新增設定

四處必改的位置、設定限制與守門測試見[設定結構](settings-schema.md#新增一個設定)。

## 測試

`tests/` 鏡像 `src/` 的資料夾結構，所以改了 `Core/Parsing/` 就去看
`tests/SqlAssist.Core.Tests/Parsing/`。只有兩個測試專案：`SqlAssist.Core.Tests`
與 `SqlAssist.Metadata.Tests`——`SqlAssist.Ssms22` 沒有測試專案，這正是
「**禁止**把只看文字就能判斷的邏輯寫進 Ssms22」的原因。

游標位置的測試寫法見 `tests/SqlAssist.Core.Tests/SqlWithCaret.cs`。
