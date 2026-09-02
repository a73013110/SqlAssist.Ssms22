# 索引

所有文件的入口，分成兩半：**上半給使用者**——裝起來、開始用；**下半給開發者**——
從「我要改什麼」或手上的檔案路徑，找到該讀的文件與該進入的程式碼。
文件本身不重複解釋內容，那些在各自的檔裡；這裡只負責指路。

只是想裝來用的話，[安裝與開始使用](getting-started.md) 一頁就夠了。

## 我只想安裝或使用

| 想做的事 | 文件 |
|---|---|
| 下載 VSIX、安裝、確認載入與問題排查 | [getting-started.md](getting-started.md) |
| 了解建議清單、欄位補全與整句展開 | [completion.md](completion.md) |
| 查 Snippet 捷徑與 Tab 欄位導航 | [snippets.md](snippets.md) |
| 了解 `SELECT *` 展開 | [wildcard-expansion.md](wildcard-expansion.md) |
| 使用滑鼠提示與完整結構預覽 | [structure-preview.md](structure-preview.md) |
| 按 F12 在新查詢視窗看物件定義 | [go-to-definition.md](go-to-definition.md) |
| 尋找功能設定 | [settings.md](settings.md) |

## 我要改的是……

以下開始是開發者的部分。動手之前先讀 [CLAUDE.md](../CLAUDE.md)——那是這個專案踩過坑
之後定下來的硬規則，每一條後面都有一次實際的事故；再讀下表對應的那一份文件。

| 想做的事 | 先讀 | 從這裡進去 |
|---|---|---|
| 建議清單多／少了某一類項目 | [completion.md](completion.md) | `Core/Completion/BuiltInSuggestionCatalog.cs`、`Ssms22/Completion/SqlAsyncCompletionSource.cs` |
| 排名順序不對 | [completion.md](completion.md) | `Core/Matching/FuzzyMatcher.cs`、`Core/Completion/SuggestionMatcher.cs` |
| 某個位置不該開清單／該開沒開 | [completion.md](completion.md) | `Core/Completion/SqlCompletionContextAnalyzer.cs`、`Core/Completion/SqlCompletionTriggers.cs` |
| 打完某個字沒有重開清單 | [completion.md](completion.md) | `Ssms22/Completion/SqlCompletionReopen.cs` |
| SSMS 自己的清單也跟著彈出來 | [completion.md](completion.md) | `Ssms22/Settings/NativeMemberList.cs`（**不要**去關內建 IntelliSense 的總開關） |
| 提交建議後寫進去的文字不對 | [completion.md](completion.md) | `Ssms22/Completion/SqlInsertionText.cs`、`SqlAsyncCompletionCommitManager.cs` |
| `INSERT INTO`／`EXEC`／`ALTER` 提交後展開的整句不對 | [completion.md](completion.md) | `Core/Statements/`（排版與規則）、`Ssms22/Completion/SqlCommitExpansions.cs` |
| 展開的整句蓋錯位置或沒有蓋上去 | [completion.md](completion.md) | `Ssms22/Completion/SqlCommitExpander.cs` |
| 關鍵字清單要增刪 | [completion.md](completion.md) | `tools/Generate-Keywords.ps1`（**不要**手改 `.Generated.cs`） |
| 內建函式、全域變數或型別要增刪 | [completion.md](completion.md) | `Core/Keywords/` 底下的 `SqlFunctionCatalog.cs`、`SqlGlobalVariableCatalog.cs`、`SqlDataTypeCatalog.cs` |
| 自動大寫的時機 | [completion.md](completion.md) | `Core/Keywords/SqlKeywordCase.cs`、`Ssms22/Editor/SqlKeywordCasing.cs` |
| `@` 或 `@@` 之後列出來的東西不對 | [completion.md](completion.md) | `Core/Completion/SqlScriptVariableSuggestions.cs`、`SqlExecutedModule.cs`、`Core/Keywords/SqlGlobalVariableCatalog.cs` |
| `別名.` 列出來的欄位不對 | [completion.md](completion.md) | `Core/Parsing/SqlScopeAnalyzer.cs`、`Core/Parsing/SqlColumnSourceResolver.cs` |
| 程式碼片段的格式、合併或展開行為 | [snippets.md](snippets.md) | `Core/Snippets/DefaultSnippets.json`、`SqlSnippetExpansion.cs`、`SqlSnippetMerger.cs`、`SqlSnippetSerializer.cs` |
| `SELECT *` 展不開或展錯 | [wildcard-expansion.md](wildcard-expansion.md) | `Core/Wildcards/SqlWildcardAnalyzer.cs` |
| 展開後的欄位排版 | [wildcard-expansion.md](wildcard-expansion.md) | `Core/Wildcards/SqlWildcardExpansionText.cs` |
| Tab／Shift+Tab 的行為 | [snippets.md](snippets.md)、[wildcard-expansion.md](wildcard-expansion.md) | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 滑鼠停留提示的內容 | [structure-preview.md](structure-preview.md) | `Ssms22/QuickInfo/SqlQuickInfoContentBuilder.cs` |
| 浮動預覽的行為或擺放 | [structure-preview.md](structure-preview.md) | `Ssms22/Preview/SqlStructurePreview.cs` |
| 任何顏色、字型、按鈕樣式 | [structure-preview.md](structure-preview.md) | `Ssms22/UI/SqlAssistChrome.cs`（**唯一**出處） |
| 按了某個鍵卻沒反應（F12 之類） | [go-to-definition.md](go-to-definition.md) | `Ssms22/Editor/SqlShellCommandFilter.cs`（**唯一**攔得到殼層命令的位置） |
| F12 抵達了卻沒開視窗 | [go-to-definition.md](go-to-definition.md) | `Ssms22/Editor/SqlDefinitionOpener.cs` |
| 新增選單項目或鍵繫結後沒生效 | [development.md](development.md) | `Menus.vsct` ＋ `ProvideMenuResource` 版號，且必須重新安裝 |
| F12 開出來的指令碼內容不對 | [go-to-definition.md](go-to-definition.md) | `Metadata/Formatting/SqlObjectScript.cs` |
| 新查詢視窗沒有沿用連線 | [go-to-definition.md](go-to-definition.md) | `Ssms22/Connections/SsmsScriptWindow.cs` |
| 新增一個設定 | [settings.md](settings.md) | 四處都要動，見下方「新增設定」 |
| 查詢的 SQL 或載入分層 | [metadata.md](metadata.md) | `Metadata/Querying/SqlMetadataQueries.cs` |
| 連不上資料庫時的行為 | [metadata.md](metadata.md) | `Metadata/Caching/SqlMetadataCatalog.cs` |
| 指令碼整段變成註解（缺定義、缺欄位） | [metadata.md](metadata.md) | `Metadata/Model/SqlObjectStructure.cs` 的 `CanBuildExecutableScript` |
| 建置、安裝、偵錯、發布 | [development.md](development.md) | `tools/` 底下的腳本，見下方「工具腳本」 |
| 分層規則、資料夾規則 | [architecture.md](architecture.md) | — |

## 資料夾對應

`src/X/Foo/` 一律是命名空間 `X.Foo`，`tests/` 鏡像同一份路徑。

### SqlAssist.Core（netstandard2.0，零 VS 相依，可完整單元測試）

| 資料夾 | 職責 | 文件 |
|---|---|---|
| `Completion/` | 建議項的模型、上下文判斷、篩選與排名 | [completion.md](completion.md) |
| `Keywords/` | 關鍵字、內建函式、全域變數與型別目錄、位置分層、自動大寫 | [completion.md](completion.md) |
| `Matching/` | 與領域無關的字串模糊比對（**禁止**參照 `Completion/`） | [completion.md](completion.md) |
| `Parsing/` | 詞法分析、註解與括號、範圍與欄位來源解析 | [completion.md](completion.md)、[architecture.md](architecture.md) |
| `Preview/` | 浮動預覽的矩形定位、避障、方向遲滯與雙側縮放 | [structure-preview.md](structure-preview.md) |
| `Snippets/` | 程式碼片段的模型、展開、佔位符與序列化 | [snippets.md](snippets.md) |
| `Statements/` | 提交後展開成整句的排版與規則（`INSERT` 骨架、`EXEC` 呼叫、參數預設值） | [completion.md](completion.md) |
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

### SqlAssist.Ssms22（net48 VSIX，只做接線）

| 資料夾 | 職責 | 文件 |
|---|---|---|
| `Completion/` | 平台原生非同步 IntelliSense 的來源、排序、提交與重開 | [completion.md](completion.md) |
| `Editor/` | 編輯器接線、Tab 與 Enter 的優先順序、非同步寫回、物件定位、大寫改寫、F12 移至定義 | [architecture.md](architecture.md)、[go-to-definition.md](go-to-definition.md) |
| `QuickInfo/` | 滑鼠停留提示 | [structure-preview.md](structure-preview.md) |
| `Preview/` | 浮動結構預覽與其專屬外觀 | [structure-preview.md](structure-preview.md) |
| `Wildcards/` | `SELECT *` 的展開與可展開提示（Tab 由 `Editor/` 分派） | [wildcard-expansion.md](wildcard-expansion.md) |
| `Snippets/` | 片段檔、管理員視窗與原生 Expansion Session | [snippets.md](snippets.md) |
| `Settings/` | Unified Settings 讀取、預覽視窗尺寸，以及推給 SSMS 的語言偏好 | [settings.md](settings.md) |
| `Connections/` | 取得 SSMS 查詢視窗的連線，以及另開一個沿用連線的查詢視窗 | [metadata.md](metadata.md)、[go-to-definition.md](go-to-definition.md) |
| `Commands/` | 命令識別碼、工具選單命令與「關於與診斷」視窗 | [development.md](development.md) |
| `UI/` | 全擴充共用的外觀與佈景筆刷 | [structure-preview.md](structure-preview.md) |

## 只有一份的東西

同一件事寫成兩份時，症狀一律是「其中一份改了另一份沒改」。要用這些功能時
一律呼叫既有的那一份，不要在自己的檔案裡再寫一次。分岔的實際症狀見
[architecture.md](architecture.md)。

| 這件事 | 唯一出處 |
|---|---|
| 略過 SQL 註解與空白 | `Core/Parsing/SqlTrivia.cs` |
| 括號配對、還沒關上的左括號、判斷括號後是不是查詢 | `Core/Parsing/SqlTokenNavigator.cs` |
| 詞法分析 | `Core/Parsing/SqlTokenizer.cs` |
| 模糊比對與命中高亮 | `Core/Matching/FuzzyMatcher.cs` |
| 識別字加括號、型別格式化 | `Metadata/Formatting/SqlIdentifier.cs`、`SqlTypeFormatter.cs` |
| 中繼資料快取與失敗降級 | `Metadata/Caching/SqlMetadataCatalog.cs` |
| 浮動預覽的落點、避障與方向遲滯 | `Core/Preview/PreviewPlacementEngine.cs` |
| 浮動預覽的雙側縮放 | `Core/Preview/PreviewResizeEngine.cs` |
| DPI 與螢幕工作區換算 | `Ssms22/Preview/NativeScreen.cs` |
| 背景結果寫回編輯器（替換既有文字與寫進空白緩衝區） | `Ssms22/Editor/TextViewEditCoordinator.cs` |
| 目前的 SQL 編輯器，以及取回剛建立的那一個 | `Ssms22/Editor/ActiveSqlEditor.cs` |
| 可執行指令碼的批次樣板與 `CREATE` → `ALTER` | `Metadata/Formatting/SqlObjectScript.cs` |
| 進度與失敗顯示在 SSMS 狀態列 | `Ssms22/SqlAssistStatusBar.cs` |
| 寫回去的多行文字用哪一種換行 | `Ssms22/Editor/SnapshotNewLine.cs` |
| 排到「這一輪命令結束之後」再做 | `Ssms22/Editor/TextViewDispatch.cs` |
| Tab／Shift+Tab／Enter 的優先順序 | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 攔截殼層命令（F12…），以及「按了沒反應」時的命令診斷 | `Ssms22/Editor/SqlShellCommandFilter.cs` |
| 提交後把整句換掉（ALTER／INSERT／EXEC 三種共用） | `Ssms22/Completion/SqlCommitExpander.cs` |
| 平台邊界的例外處理 | `Ssms22/SqlAssistPlatformGuard.cs` |
| 版本顯示、健康檢查，以及「關於與診斷」與匿名摘要共用的欄位 | `Core/Diagnostics/` |
| 重開建議清單的三個步驟 | `Ssms22/Completion/SqlCompletionReopen.cs` |
| Snippet 純文字、游標與欄位位置的計算 | `Core/Snippets/SqlSnippetExpansion.cs` |
| SQL 語言服務 GUID | `Ssms22/SqlLanguageService.cs` |
| 擋掉 SSMS 內建的自動建議清單 | `Ssms22/Settings/NativeMemberList.cs` |
| 字型、按鈕、輸入欄位、資料格樣板 | `Ssms22/UI/SqlAssistChrome.cs` |
| 佈景主題筆刷 | `Ssms22/UI/VsThemeBrushes.cs` |
| 腳本的 SSMS 路徑與擴充 Id 探索 | `tools/SqlAssist.Tools.psm1` |

## 新增設定

四處要一起動，漏掉後三處的任何一處都是**建置失敗**，不是執行期回退。
理由與 Unified Settings 的限制見 [settings.md](settings.md)。

1. `src/SqlAssist.Ssms22/SqlAssist.registration.json`
2. `src/SqlAssist.Core/Settings/SqlAssistSettings.cs`（屬性）
3. `src/SqlAssist.Core/Settings/SqlAssistMonikers.cs`（常數）
4. `src/SqlAssist.Core/Settings/SqlAssistSettingsReader.cs`（`Read()` 對應）

數值設定的合理範圍另外寫在 `Core/Settings/SqlAssistLimits.cs`。
守門的測試是 `tests/SqlAssist.Core.Tests/Settings/SqlAssistRegistrationTests.cs`。

## 工具腳本

全部細節見 [development.md](development.md)。

| 腳本 | 做什麼 |
|---|---|
| `Run-CoreTests.ps1` | 以方案為目標跑單元測試（執行器由 `global.json` 指定） |
| `Build-Extension.ps1` | 建置並產出 VSIX |
| `Install-Extension.ps1` | 以官方 VSIXInstaller 安裝 |
| `Uninstall-Extension.ps1` | 解除安裝（保留使用者設定與紀錄） |
| `Deploy-DebugExtension.ps1` | 部署 Debug 組件並**清除 MEF 快取**，供 F5 偵錯 |
| `Show-Diagnostics.ps1` | 顯示安裝狀態與最近的診斷紀錄 |
| `Generate-Keywords.ps1` | 以 ScriptDom 重新產生 `SqlKeywordCatalog.Generated.cs` |
| `Publish-Release.ps1` | 建置、驗證並建立 GitHub 草稿 Release |
| `Test-VsixPackage.ps1` | 檢查 VSIX 套件結構 |
| `Test-CommandTable.ps1` | 交叉驗證 VSCT、`CommandIds` 與註冊檔的命令識別碼 |
| `SqlAssist.Tools.psm1` | 上述腳本共用的環境探索 |

## 測試

`tests/` 鏡像 `src/` 的資料夾結構，所以改了 `Core/Parsing/` 就去看
`tests/SqlAssist.Core.Tests/Parsing/`。只有兩個測試專案：`SqlAssist.Core.Tests`
與 `SqlAssist.Metadata.Tests`——`SqlAssist.Ssms22` 沒有測試專案，這正是
「**禁止**把只看文字就能判斷的邏輯寫進 Ssms22」的原因。

游標位置的測試寫法見 `tests/SqlAssist.Core.Tests/SqlWithCaret.cs`。
