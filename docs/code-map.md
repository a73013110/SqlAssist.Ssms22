# 詳細程式碼路徑表

本頁包含「症狀 → 從哪個型別進去」；文件路由在[索引](index.md)，唯一實作在[共用元件表](shared-components.md)。

## 我要改的是……

| 想做的事 | 從這裡進去 |
| --- | --- |
| 建議清單多／少了某一類項目 | `Core/Completion/BuiltInSuggestionCatalog.cs`、`Ssms22/Completion/SqlAsyncCompletionSource.cs` |
| 排名順序不對 | `Core/Matching/FuzzyMatcher.cs`、`Core/Completion/SuggestionMatcher.cs` |
| 某個位置不該開清單／該開沒開 | `Core/Completion/SqlCompletionContextAnalyzer.cs`、`Core/Completion/SqlCompletionTriggers.cs` |
| 打完某個字沒有重開清單 | `Ssms22/Completion/SqlCompletionReopen.cs` |
| 換了介面語言某處沒跟著換 | `Ssms22/Settings/SqlLanguageSwitch.cs`（誰訂閱了 `Changed`）；各表面的作法見[在地化](localization.md#即時切換) |
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
| 包住選取範圍的清單、縮排或觸發 | `Core/Snippets/SqlSnippetSurround.cs`、`Ssms22/Snippets/SqlSnippetSurroundAction.cs` |
| `SELECT *` 展不開或展錯 | `Core/Wildcards/SqlWildcardAnalyzer.cs` |
| 展開後的欄位排版 | `Core/Wildcards/SqlWildcardExpansionText.cs` |
| Tab／Shift+Tab 的行為 | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 滑鼠停留提示的內容 | `Ssms22/QuickInfo/SqlQuickInfoContentBuilder.cs` |
| 函式參數提示浮不出來、粗體停在錯的引數 | `Ssms22/Signatures/SqlSignatureHelp.cs`、`Core/Completion/SqlCallSignature.cs` |
| 浮動預覽的行為或擺放 | `Ssms22/Preview/SqlStructurePreview.cs` |
| 任何自製 UI、顏色、字型或排版 | `Ssms22/UI/SqlAssistChrome.cs`（**唯一**出處） |
| 按了某個鍵卻沒反應（F12 之類） | `Ssms22/Editor/SqlShellCommandFilter.cs` |
| F12 抵達了卻沒開視窗 | `Ssms22/Editor/SqlDefinitionOpener.cs` |
| Ctrl＋點擊沒有底線或點了沒反應 | `Ssms22/Editor/SqlClickNavigator.cs` |
| 結果格線右鍵選單的命令、產出的 SQL 不對 | `Metadata/ResultGrid/`、`Ssms22/ResultGrid/` |
| 新增選單項目或鍵繫結後沒生效 | `Menus.vsct` ＋ `ProvideMenuResource` 版號，且必須重新安裝 |
| F12 開出來的指令碼內容不對 | `Metadata/Formatting/SqlObjectScript.cs` |
| 新查詢視窗沒有沿用連線 | `Ssms22/Connections/SsmsScriptWindow.cs`；SQL Search 別台的結果刻意不連，判斷在 `Search/SqlSearchActivation.cs` |
| 換了資料庫，清單還是舊資料庫的物件 | `Ssms22/Connections/SqlMetadataService.cs`、`SqlEditorConnectionWatcher.cs` |
| 新增一個設定 | 見[設定護欄](rules-settings.md) |
| 查詢的 SQL 或載入分層 | `Metadata/Querying/SqlMetadataQueries.cs` |
| 連不上資料庫時的行為 | `Metadata/Caching/SqlMetadataCatalog.cs` |
| SQL Memory 開不起來、停用後還在擷取、心跳或維護沒跑 | `Core/SqlMemory/SqlMemoryRuntime.cs`（`Ssms22/SqlMemory/SqlMemoryHost.cs` 只接線） |
| SQL Memory 清單篩選、分頁、晚到回應或選取還原 | `Core/SqlMemory/SqlMemoryBrowserModel.cs` |
| 伺服器／資料庫篩選或套用查詢視窗的連線不對（兩個工具窗） | `Core/Connections/SqlConnectionScope.cs`；Search 的連線在 `Search/SqlSearchCatalogs.cs` |
| 大小寫、整個字沒作用，或同一個字在兩個工具窗命中不同 | `Core/Matching/TextMatcher.cs`；開關在 `Ssms22/UI/SqlMatchToggles.cs` |
| 多選勾不起來、勾錯列、選取工具列不出現 | `Ssms22/UI/SqlCardSelection.cs`、`SqlCardList.cs`、`SqlSelectionBar.cs` |
| 批次複製的欄位、順序或「全部符合」讀不完 | `Core/SqlMemory/SqlMemoryCopy.cs`、`SqlMemoryBulk.cs`、`Core/Tabular/SqlTabularText.cs`、`Ssms22/SqlMemory/SqlMemoryBrowser.cs`；Search 的欄位在 `Search/SqlSearchRow.cs` |
| 批次刪除的分批、取消或衝突 | `Core/SqlMemory/SqlMemoryDeletion.cs`、`Ssms22/SqlMemory/SqlMemoryItemCommands.cs` |
| 存檔後歷程掛錯文件、選取執行記錄的文字不對 | `SqlDocumentIdentity.cs`、`SqlSelectionText.cs`、`Ssms22/SqlMemory/SqlCaptureTracker.cs` |
| SQL Memory 的 SQL、交易或索引 | `SqlMemory.Sqlite/Sqlite*Store.cs`（連線與 schema 在 `SqliteDatabase.cs`） |
| 指令碼整段變成註解（缺定義、缺欄位） | `Metadata/Model/SqlObjectStructure.cs` 的 `CanBuildExecutableScript` |
| 通知島錨錯視窗、不出現或提醒按鈕沒反應 | `Ssms22/Notifications/`，分工見[通知呈現](notifications-ui.md#分工) |

## 測試

`SqlAssist.SqlMemory.Sqlite.Tests` 驗真實 SQLite 與隔離層；`SqlAssist.Ssms22.Tests` 只連結純 WPF
控制項做渲染測試，不載入 SSMS——所以只看文字就能判斷的邏輯要放 Core 才測得到。
游標位置的測試寫法見 `tests/SqlAssist.Core.Tests/SqlWithCaret.cs`。
