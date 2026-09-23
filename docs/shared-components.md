# 共用元件表

範圍：新增或改動共用邏輯前查找唯一出處，避免再造一份。返回 [索引](index.md)。

本頁是 Core 與 Metadata 的純邏輯；Ssms22 接線層與工具腳本見
[平台共用元件](shared-components-platform.md)，SQL Memory 見[專屬表](shared-components-sql-memory.md)。直接重用下列實作，不在功能目錄重寫，
以免行為分岔。

| 這件事 | 唯一出處 |
| --- | --- |
| 一個名稱有幾段、哪一段是什麼（右對齊、空的中間段、段數上限） | `Core/Parsing/SqlObjectPath.cs` |
| 把連線指向同一台伺服器的另一個資料庫 | `Metadata/Querying/SqlDatabaseScopedConnectionSource.cs` |
| 把查詢指向連結伺服器（`OPENQUERY` 包裝、`sys.` 限定字、內嵌 object_id） | `Metadata/Querying/SqlCatalogQualifier.cs` |
| 認出限定字最左邊那一段是結構描述、資料庫還是連結伺服器 | `Metadata/Model/SqlQualifierResolver.cs` |
| 目錄的快取鍵怎麼組（伺服器＋資料庫＋連結伺服器） | `Metadata/Querying/SqlConnectionCacheKey.cs` |
| 略過 SQL 註解與空白 | `Core/Parsing/SqlTrivia.cs` |
| 括號配對、還沒關上的左括號、判斷括號後是不是查詢、往回跳過限定名稱 | `Core/Parsing/SqlTokenNavigator.cs` |
| 分辨 `ON` 後面是資料表還是述詞 | `Core/Parsing/SqlDdlTarget.cs` |
| 讀出暫存資料表與資料表變數的資料行 | `Core/Parsing/SqlScriptTableCollector.cs` |
| 指令碼宣告的資料來源換成物件明細（含宣告原文） | `Metadata/Model/SqlScriptTableDetail.cs` |
| 拿名稱向這份指令碼換宣告（Hover、預覽與 F12 共用，名稱決定種類） | `Metadata/Model/SqlScriptDeclarations.cs` |
| 詞法分析 | `Core/Parsing/SqlTokenizer.cs` |
| 區塊配對與祖先查詢 | `Core/Parsing/BlockMatcher.cs` |
| 模糊比對與命中高亮 | `Core/Matching/FuzzyMatcher.cs` |
| 識別字加括號（形狀、保留字、指令碼自己宣告的名稱） | `Core/Parsing/SqlIdentifier.cs` |
| 提交建議時寫進編輯器的文字（補不補結構描述、要不要方括號） | `Core/Completion/SqlInsertionText.cs` |
| 型別格式化 | `Metadata/Formatting/SqlTypeFormatter.cs` |
| 中繼資料快取與失敗降級 | `Metadata/Caching/SqlMetadataCatalog.cs` |
| 篩選名單的去空白、去重、排序與游標指紋（伺服器／資料庫三份請求共用） | `Core/SqlMemory/SqlConnectionNames.cs` |
| 範圍列的伺服器與資料庫篩選（單選／多選、換伺服器清資料庫、套用查詢視窗的連線；Memory 與 Search 共用） | `Core/Connections/SqlConnectionScope.cs` |
| 一個物件掛在誰身上（父物件、子物件的型別代碼、DEFAULT 的資料行） | `Metadata/Caching/SqlMetadataCatalog.cs` 的 `GetParentAsync` |
| 物件總管節點的 URN（節點路徑、候選順序與跳脫） | `Metadata/Model/SqlObjectExplorerUrn.cs` |
| 一輪搜尋的排名、去重、合併與預算世代作廢 | `Core/Search/SearchAggregator.cs`（被併掉的那幾筆在 `SearchHit.Merged`） |
| 一份文字上「有幾處命中、現在停在第幾處、上下一處與環繞」 | `Core/Matching/MatchCursor.cs` |
| 命中的併段、標記上限與狀態列那一句（Search 與 Memory 預覽共用；各功能只決定拿什麼找） | `Core/Matching/MatchHighlights.cs` |
| 字面比對（大小寫、全字、重疊與上限；字串與 UTF-16LE 位元組同一套規則；Search 與 Memory 都走它） | `Core/Matching/TextMatcher.cs`，選項是 `TextMatchOptions` |
| 比對選項的驗證與記住的字串格式 | `Core/Matching/TextMatchState.cs` |
| 片段上的高亮區段平移到整份文字 | `Core/Matching/MatchProjection.cs` 的 `Shift` |
| 名稱與資料行的命中怎麼比（沒開修飾走模糊，開了大小寫或全字走字面） | `Core/Search/SearchIdentifierMatch.cs` |
| 搜尋索引的位元組預算、版本戳與失效 | `Metadata/Search/SqlCatalogSearchIndexCache.cs`（與 `SqlMetadataCatalog` 分開，不合併） |
| Hover、結構面板與 F12 的物件／欄位定位 | `Metadata/Model/SqlObjectLookup.cs`（先問指令碼再問快照；語法可重用，資料每次重新比對） |
| 結果格線的值轉成 T-SQL 字面值 | `Metadata/ResultGrid/SqlValueLiteral.cs` |
| 浮動預覽的落點、避障與方向遲滯 | `Core/Preview/PreviewPlacementEngine.cs` |
| 浮動預覽的雙側縮放 | `Core/Preview/PreviewResizeEngine.cs` |
| 重建 `CREATE TABLE`／`CREATE TYPE`、索引、條件約束與擴充屬性的排版 | `Metadata/Formatting/TSqlScriptRenderer.cs` |
| 單獨一個條件約束是哪一種、在父物件上的哪一列 | `Metadata/Model/SqlConstraintMatch.cs`、`SqlObjectStructure.FindConstraint` |
| 指令碼的所有開關與三組具名風格 | `Core/Scripting/SqlScriptOptions.cs` |
| 擴充屬性的 `sp_addextendedproperty` 八個引數 | `Metadata/Formatting/SqlExtendedPropertyScript.cs` |
| 說明收成單行與截斷（提示、說明面板與預覽共用） | `Metadata/Formatting/SqlDescriptionText.cs` |
| 內建名稱的簽章、用途與範例（提示、說明面板與浮動預覽共用） | `Core/Keywords/SqlBuiltInDocCatalog.cs` |
| 檔頭、健檢與降級摘要的逐行 SQL 註解 | `Metadata/Formatting/SqlScriptComment.cs` |
| 索引選項的預設值是什麼 | `Metadata/Model/SqlIndexOptions.cs` |
| 結構健檢的規則集合與失敗隔離 | `Metadata/Analysis/SqlSchemaAnalyzer.cs` |
| 送進查詢視窗前的換行統一與游標落點 | `Metadata/Formatting/SqlObjectScript.cs` |
| 同義字與序列的 `CREATE` 定義（目錄檢視組回 T-SQL） | `Metadata/Formatting/SqlCatalogScript.cs` |
| 分隔字元自動配對的判斷，以及「這一個是我補的」 | `Core/Pairing/SqlAutoPairAnalyzer.cs`、`Ssms22/Editor/SqlAutoPairing.cs` |
| 版本顯示、健康檢查，以及「關於與診斷」與匿名摘要共用的欄位 | `Core/Diagnostics/` |
| 通知標題、完成後的敘述與狀態措辭 | `Core/Notifications/NotificationCatalog.cs` |
| 通知計數、結果保留與近期失敗 | `Core/Notifications/NotificationCenter.cs` |
| 通知可見度規則（三軸、詳細度門檻、獨立通道） | `Core/Notifications/NotificationVisibility.cs` |
| 通知種類的 moniker、預設值與標題 | `Core/Notifications/NotificationKindToggle.cs` |
| Snippet 展開／欄位／縮排 | `Core/Snippets/SqlSnippetExpansion.cs`、`SqlSnippetIndentation.cs` |
| 表格文字：TSV（Excel 引號規則）與 CF_HTML 表格，同一趟寫完 | `Core/Tabular/SqlTabularText.cs`（欄位定義留在各功能） |
| 區塊色彩 | [唯一實作](block-colors.md) |
