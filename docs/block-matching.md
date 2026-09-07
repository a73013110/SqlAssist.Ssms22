# T-SQL 區塊配對

## 架構與查詢契約

`Core/Parsing/BlockMatcher` 是不可變的文字分析結果，不參照 VS／SSMS。
`SqlTokenizer.TokenizeBlocks` 使用 `TSql160Parser.GetTokenStream`；括號索引沿用
`SqlTokenNavigator`，不另寫字串或註解掃描。

- 配對 BEGIN、TRY、CATCH、CASE、圓括號、方括號識別字及單引號字串的外框。
- 字串沿用 ScriptDom 的完整詞元，支援空字串、Unicode 前綴與多行；不把跳脫的 `''`、
  `N` 前綴或字串內 SQL 當端點，未閉合字串不配對。
- 複合端點分成詞元：`BEGIN /* 註解 */ TRY` 的中間不算高亮範圍。
- 交易與 Service Broker 的 BEGIN／END 不當區塊；批次分隔 GO 清除未閉合堆疊。
- 錯配不向外搶 END；未閉合殘留與交叉區間忽略，已完成的內層保留。
- 位置使用 UTF-16 半開區間；`FindPairAt` 不把詞元後方空白視為端點。
- `FindPairAt`、`GetEnclosingBlock` 為 O(log n)；`GetAncestors` 由內而外，
  為 O(log n + depth)，包含位置所在的區塊本身。

`Ssms22/Blocks/BlockAnalysis` 每個文字緩衝區只有一份，以 snapshot version 檢查結果。
分割檢視共用解析但各自選擇高亮；停止輸入 150 ms 後在背景取全文與重算。
同一 buffer 最多一份 ScriptDom 解析進行中，過期結果不得發布。官方 tokenizer
沒有取消 API，因此已進入其中的呼叫要等它返回，但不會阻塞 UI。
`BlockAnalysisWorker` 負責有界排程；`BlockViewState` 合併游標與設定通知。
最後一個檢視／buffer tagger 釋放時取消工作、移除設定與 buffer 事件；`GetTags` 不解析文字。
結構 Tag 使用 `GetIntersectingBlocks` 查詢要求範圍，約 O(log n + 命中數)，不逐頁掃全文。

未來標準命令掛接點標在 `Editor/SqlShellCommandFilter.Exec` 的 TODO；本次不新增鍵繫結。

## 設定與呈現

沿用工具 → 選項的 SqlAssist Unified Settings，新增「區塊配對」分類。
子項目透過同分類 `enableWhen` 縮排與灰階；設定通知派送至檢視 UI 執行緒後即時更新。
括號／CASE 開關只過濾呈現，不刪掉解析結果，以免改變其他 END 的歸屬。

| 呈現 | 預設 | 接線 |
|---|---|---|
| A 端點高亮 | 開 | view 級 `ClassificationTag`，關鍵字／括號各有獨立前背景分類 |
| B 區間背景 | 開 | 同一 Tagger，`SqlAssist.BlockRange` 格式、最高約 12% 不透明度 |
| C 導引線 | 開 | buffer 級 `ITagger<IStructureTag>`，原生 Shell 繪製；補充摺疊另設，預設關 |
| D 邊欄色帶 | 開 | `IGlyphFactoryProvider`，只為要求的行建立色帶 |
| E 捲軸概覽 | 開 | `VerticalScrollBar` 內的兩端與範圍色帶，使用原生 scroll map |
| F 跨頁提示 | 關 | 視窗相對 Adornment，有界摘要，按鈕可返回起始行 |

A 跟隨游標所在的配對端點；B 預設在區塊內顯示最近符合設定的一層，不疊加背景。
同行背景關閉時略過同行小括號，繼續顯示外層跨行區塊；可關閉「區塊內也顯示」恢復端點模式。
D／E 跟隨 B 的範圍，沒有背景範圍時仍能獨立顯示最近區塊；F 顯示游標所在區塊的上下文。
括號／字串與 CASE、同行背景均預設開啟；Debounce 預設 150 ms（可設 50–2000）。
背景行為、端點顏色與補充摺疊各自縮排在所屬功能下；各呈現可獨立啟用。
新預設不覆蓋使用者已儲存的值，既有設定要手動調整或逐項恢復預設。

配色、原生選色器、設定層級與導引線的自訂方式見[區塊配色](block-colors.md)。
A 只覆寫當前端點的前背景，不改 SQL 全域分類色、字型或原生 `bracehighlight`。

`BlockPalette` 集中推導淡底、色帶與提示；`EditorBlockTheme` 每個檢視只保留一份動態
資源。A 端點背景及 D／E 與編輯器底色至少 3:1，B 最高約 12% 不透明度並保護一般文字對比；不保證
任意自訂 SQL 分類色均有相同對比。F 使用不透明淡底及至少 4.5:1 文字，避免 SQL 穿透。
高對比不塗 B，其他區塊提示使用系統配對色，不套自訂色。

主題、編輯器底色、字色及基準色變更合併至 UI 執行緒；筆刷凍結、同色不替換，關閉
檢視解除訂閱。配色不啟動 SQL 重解析，游標與捲動不重算色票。
F 只顯示起始行的有界摘要，獨立關鍵字只列種類與行號；不把上一行猜成控制條件。
目前配對索引不是控制流程語法樹，未來若加入條件摘要，應由共用分析結果提供已確認歸屬。
F 導覽先檢查 snapshot，過期提示不移動游標；保留自動化名稱，但不占用鍵盤焦點。

## 平台探測結論

- SSMS 實機 `ContentType=SQL`、基底 `code`，`bracehighlight` 有 Fill 與 ZOrder。
- A 現用分類標籤，因 `MarkerFormatDefinition.Foreground` 是框線而不是字色。
- D 的行標記不可包含換行：原生 margin 以含邊界的 `IntersectsWith` 移除重繪行圖示，
  含換行的前一行會碰到下一行起點，造成捲動後缺漏；空白行用零長度 extent。
- 原生 outlining 轉接器使用 **buffer** aggregator；C 不可只匯出 view tagger。
- Aggregator 會於內容類型變更時 Dispose 並重建 Tagger；A／C／D 各次提供新 Tagger，只共用分析與檢視狀態。
- Marker renderer 優先讀 `BackgroundColor`，所以 B 的自訂色也要套透明度，不能只改 Fill。
- ViewportRelative 只自動處理後續位移；F 初次加入仍須使用 `ViewportTop／Left` 文件座標。
- 不更動 SSMS 自己的大綱或原生導引線開關；其顯示條件可能影響實機結果。

實機狀態與操作步驟見[驗收](block-matching-validation.md)。
實作參考 [Microsoft 的配對 Tagger 範例](https://learn.microsoft.com/en-us/visualstudio/extensibility/walkthrough-displaying-matching-braces?view=visualstudio)。
