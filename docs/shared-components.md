# 共用元件表

本頁只列 Core 純邏輯的唯一出處，直接重用、不在功能目錄重寫；Metadata 見[中繼資料共用元件](shared-components-metadata.md)，
Ssms22 接線層見[平台共用元件](shared-components-platform.md)，SQL Memory 見[專屬表](shared-components-sql-memory.md)。

| 這件事 | 唯一出處 |
| --- | --- |
| 一個名稱有幾段、哪一段是什麼（右對齊、空的中間段、段數上限） | `Core/Parsing/SqlObjectPath.cs` |
| 略過 SQL 註解與空白 | `Core/Parsing/SqlTrivia.cs` |
| 括號配對、未關上的左括號、括號後是不是查詢、往回跳過限定名稱 | `Core/Parsing/SqlTokenNavigator.cs` |
| 運算式從哪裡開始 | `Core/Keywords/SqlOperand.cs` |
| 分辨 `ON` 後面是資料表還是述詞 | `Core/Parsing/SqlDdlTarget.cs` |
| 讀出暫存資料表與資料表變數的資料行 | `Core/Parsing/SqlScriptTableCollector.cs` |
| 指令碼宣告的物件建議 | `Core/Completion/SqlScriptObjectSuggestions.cs` |
| 資料指標名稱；視窗規格的左括號與 WINDOW 子句的名稱 | `Core/Parsing/SqlCursorDeclaration.cs`；`SqlWindowClause.cs` |
| 詞法分析；字串常值讀寫 | `Core/Parsing/SqlTokenizer.cs`、`SqlStringLiteral.cs` |
| 區塊配對與祖先查詢 | `Core/Parsing/BlockMatcher.cs` |
| 模糊比對與命中高亮 | `Core/Matching/FuzzyMatcher.cs` |
| 建議清單開不開、軟硬選；排名、無前綴可見度、分類篩選 | `Core/Completion/SqlCompletionPolicy.cs`；`SuggestionList.cs` |
| 識別字加括號與拿掉括號（形狀、保留字、宣告的名稱）；正在打的左方括號與它的右半截 | `Core/Parsing/SqlIdentifier.cs` |
| 提交建議時寫進編輯器的文字（補不補結構描述、要不要方括號） | `Core/Completion/SqlInsertionText.cs` |
| 候選分派（資料庫由介面注入）、中繼資料轉建議項 | `Core/Completion/SqlCompletionCandidates.cs`；`Metadata/Completion/` |
| 定序、語言、時區名單的位置、已用值、排名 | `Core/Completion/SqlInstanceList.cs` |
| 篩選名單的去空白、去重、排序與游標指紋 | `Core/SqlMemory/SqlConnectionNames.cs` |
| 範圍列的伺服器與資料庫篩選 | `Core/Connections/SqlConnectionScope.cs` |
| 一輪搜尋的排名、去重、合併與世代作廢；各目標完整度與進度 | `Core/Search/SearchAggregator.cs`、`SearchTarget.cs`、`SearchRun.cs` |
| 還要多久（進度平滑、說不準時不說） | `Core/Search/SearchEta.cs` |
| 一份文字有幾處命中、停在第幾處、上下一處與環繞 | `Core/Matching/MatchCursor.cs` |
| 命中的併段、標記上限與少標了那一句 | `Core/Matching/MatchHighlights.cs` |
| 字面比對（大小寫、全字、重疊、上限、UTF-16LE）與選項 | `Core/Matching/TextMatcher.cs`、`TextMatchState.cs` |
| 片段上的高亮區段平移到整份文字 | `Core/Matching/MatchProjection.cs` 的 `Shift` |
| 名稱與資料行的命中怎麼比（沒開修飾走模糊，開了大小寫或全字走字面） | `Core/Search/SearchIdentifierMatch.cs` |
| 浮動預覽的落點、避障與方向遲滯；搬動、縮放與收進界內 | `Core/Preview/PreviewPlacementEngine.cs`、`PreviewDragEngine.cs` |
| 浮動預覽何時收、釘住的窗借用與還回、膠囊停多久 | `Core/Preview/PreviewLifecycle.cs`、`PreviewReveal.cs` |
| 指令碼的所有開關與三組具名風格 | `Core/Scripting/SqlScriptOptions.cs` |
| 內建名稱的簽章、用途與範例；游標處或清單候選是不是語句 | `Core/Keywords/SqlBuiltInDocCatalog.cs`（清單端記答案 `SqlStatementCandidates.cs`） |
| 分隔字元自動配對的判斷與「這一個是我補的」 | `Core/Pairing/SqlAutoPairAnalyzer.cs`、`Ssms22/Editor/SqlAutoPairing.cs` |
| 版本顯示、健康檢查，「關於與診斷」與匿名摘要共用的欄位 | `Core/Diagnostics/` |
| 介面文字與目前語言（取值、切換、固定語言的範圍、句子外的數字） | `Core/Localization/SqlText.cs`；文字在各資料夾的 `*.resjson` |
| 跨功能共用詞與 SQL 種類名稱 | `Core/Localization/CommonText`、`SqlKindText`（`.resjson`） |
| 內嵌 JSON 的譯文與含譯文的快取 | `Core/Localization/SqlTextOverlay.cs`、`SqlLanguageCache.cs` |
| 通知標題、敘述、狀態措辭、提醒與膠囊摘要 | `Core/Notifications/NotificationCatalog.cs` |
| 提醒按鈕的穩定識別字 | `Core/Notifications/NotificationActionIds.cs` |
| 通知計數、結果保留與近期失敗 | `Core/Notifications/NotificationCenter.cs` |
| 通知可見度規則（三軸、詳細度門檻、獨立通道） | `Core/Notifications/NotificationVisibility.cs` |
| 通知種類的 moniker、預設值與標題 | `Core/Notifications/NotificationKindToggle.cs` |
| Snippet 展開／欄位／縮排 | `Core/Snippets/SqlSnippetExpansion.cs`、`SqlSnippetIndentation.cs` |
| 片段在預覽顯示的文字 | `Core/Snippets/SqlSnippetPreview.cs` |
| 表格文字：TSV（Excel 引號規則）與 CF_HTML 表格，同一趟寫完 | `Core/Tabular/SqlTabularText.cs`（欄位定義留在各功能） |
| 清單頁尾的狀態（筆數、部分結果、續頁、說明的語氣）與分頁世代 | `Core/Lists/SqlListFooter.cs`、`PagedLoadState.cs` |
| 區塊色彩 | [唯一實作](block-colors.md) |
