# 共用元件表

範圍：新增或改動共用邏輯前查找唯一出處，避免再造一份。返回 [索引](index.md)。

同一件事寫成兩份時，症狀一律是「其中一份改了另一份沒改」。要用這些功能時
直接呼叫下列既有實作，不要在功能目錄重寫。

| 這件事 | 唯一出處 |
|---|---|
| 一個名稱有幾段、哪一段是什麼（右對齊、空的中間段、段數上限） | `Core/Parsing/SqlObjectPath.cs` |
| 把連線指向同一台伺服器的另一個資料庫 | `Metadata/Querying/SqlDatabaseScopedConnectionSource.cs` |
| 把查詢指向連結伺服器（`OPENQUERY` 包裝、`sys.` 限定字、內嵌 object_id） | `Metadata/Querying/SqlCatalogQualifier.cs` |
| 認出限定字最左邊那一段是結構描述、資料庫還是連結伺服器 | `Metadata/Model/SqlQualifierResolver.cs` |
| 目錄的快取鍵怎麼組（伺服器＋資料庫＋連結伺服器） | `Metadata/Querying/SqlConnectionCacheKey.cs` |
| 略過 SQL 註解與空白 | `Core/Parsing/SqlTrivia.cs` |
| 括號配對、還沒關上的左括號、判斷括號後是不是查詢、往回跳過限定名稱 | `Core/Parsing/SqlTokenNavigator.cs` |
| 分辨 `ON` 後面是資料表還是述詞 | `Core/Parsing/SqlDdlTarget.cs` |
| 讀出暫存資料表與資料表變數的資料行 | `Core/Parsing/SqlScriptTableCollector.cs` |
| 詞法分析 | `Core/Parsing/SqlTokenizer.cs` |
| 模糊比對與命中高亮 | `Core/Matching/FuzzyMatcher.cs` |
| 識別字加括號（形狀、保留字、指令碼自己宣告的名稱） | `Core/Parsing/SqlIdentifier.cs` |
| 提交建議時寫進編輯器的文字（補不補結構描述、要不要方括號） | `Core/Completion/SqlInsertionText.cs` |
| 型別格式化 | `Metadata/Formatting/SqlTypeFormatter.cs` |
| 中繼資料快取與失敗降級 | `Metadata/Caching/SqlMetadataCatalog.cs` |
| 結果格線的值轉成 T-SQL 字面值 | `Metadata/ResultGrid/SqlValueLiteral.cs` |
| 從 SSMS 結果格線取資料（兩套欄索引只換算一次） | `Ssms22/ResultGrid/SsmsResultGrid.cs` |
| 浮動預覽的落點、避障與方向遲滯 | `Core/Preview/PreviewPlacementEngine.cs` |
| 浮動預覽的雙側縮放 | `Core/Preview/PreviewResizeEngine.cs` |
| DPI 與螢幕工作區換算 | `Ssms22/Preview/NativeScreen.cs` |
| 背景結果寫回編輯器（替換既有文字與寫進空白緩衝區） | `Ssms22/Editor/TextViewEditCoordinator.cs` |
| 目前的 SQL 編輯器，以及取回剛建立的那一個 | `Ssms22/Editor/ActiveSqlEditor.cs` |
| 可執行指令碼的批次樣板與 `CREATE` → `ALTER` | `Metadata/Formatting/SqlObjectScript.cs` |
| 同義字與序列的 `CREATE` 定義（目錄檢視組回 T-SQL） | `Metadata/Formatting/SqlCatalogScript.cs` |
| 進度與失敗顯示在 SSMS 狀態列 | `Ssms22/SqlAssistStatusBar.cs` |
| 寫回去的多行文字用哪一種換行 | `Ssms22/Editor/SnapshotNewLine.cs` |
| 排到「這一輪命令結束之後」再做 | `Ssms22/Editor/TextViewDispatch.cs` |
| Tab／Shift+Tab／Enter 的優先順序 | `Ssms22/Editor/SqlTabCommandHandler.cs` |
| 分隔字元自動配對的判斷，以及「這一個是我補的」 | `Core/Pairing/SqlAutoPairAnalyzer.cs`、`Ssms22/Editor/SqlAutoPairing.cs` |
| 攔截殼層命令（F12…），以及「按了沒反應」時的命令診斷 | `Ssms22/Editor/SqlShellCommandFilter.cs` |
| 提交後改寫文字（ALTER／INSERT／MERGE／EXEC／函式引數五種共用） | `Ssms22/Completion/SqlCommitExpander.cs` |
| 平台邊界的例外處理 | `Ssms22/SqlAssistPlatformGuard.cs` |
| 版本顯示、健康檢查，以及「關於與診斷」與匿名摘要共用的欄位 | `Core/Diagnostics/` |
| 重開建議清單的三個步驟 | `Ssms22/Completion/SqlCompletionReopen.cs` |
| Snippet 純文字、游標與欄位位置的計算 | `Core/Snippets/SqlSnippetExpansion.cs` |
| SQL 語言服務 GUID | `Ssms22/SqlLanguageService.cs` |
| 擋掉 SSMS 內建的自動建議清單 | `Ssms22/Settings/NativeMemberList.cs` |
| 字型、按鈕、輸入欄位、資料格樣板 | `Ssms22/UI/SqlAssistChrome.cs` |
| SQL 圖示（補全、結構預覽與 QuickInfo 的原生圖示及快取） | `Ssms22/UI/SqlIcons.cs` |
| 佈景主題筆刷 | `Ssms22/UI/VsThemeBrushes.cs` |
| 主題色階推導與雙表面對比 | `Ssms22/UI/ThemePalette.cs`、`ThemeColorMath.cs` |
| 動態配色資源與合併更新通知 | `Ssms22/UI/ThemeResourceSet.cs`、`ThemeRefreshQueue.cs` |
| 腳本的 UTF-8 輸出、SSMS 路徑與擴充 Id 探索 | `tools/SqlAssist.Tools.psm1` |
