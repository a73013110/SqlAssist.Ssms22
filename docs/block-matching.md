# T-SQL 區塊配對

## 架構與查詢契約

`Core/Parsing/BlockMatcher` 是不可變的文字分析結果，不參照 VS／SSMS。
`SqlTokenizer.TokenizeBlocks` 使用 `TSql160Parser.GetTokenStream`；括號索引沿用
`SqlTokenNavigator`，不另寫字串或註解掃描。

- 配對 BEGIN、TRY、CATCH、CASE、圓括號及方括號識別字的外框。
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
| A 關鍵字高亮 | 開 | `TextMarkerTag("bracehighlight")` |
| B 區間背景 | 開 | 同一 Tagger，`SqlAssist.BlockRange` 格式、約 8% 透明度 |
| C 導引線與摺疊 | 開 | buffer 級 `ITagger<IStructureTag>`，原生 Shell 繪製 |
| D 邊欄色帶 | 關 | `IGlyphFactoryProvider`，只為要求的行建立色帶 |
| E 捲軸概覽 | 開 | `VerticalScrollBar` 內的 margin，使用原生 scroll map |
| F 跨頁提示 | 開 | 視窗相對 Adornment，有界摘要且不攔截輸入 |

A、B、D、E 跟隨游標所在的配對端點；F 也可顯示游標所在區塊的上下文。
括號與 CASE 預設開啟、同行背景預設關閉、Debounce 預設 150 ms（可設 50–2000）。
所有子項目直接縮排在區塊總開關下；可同時啟用，關掉任一呈現不會強制關掉其他項目。

關鍵字不覆寫使用者的字型與色彩；B 的自訂色可在字型與色彩中選「SqlAssist 區塊區間背景」。
色帶配色由共用主題色相推導；高對比不塗 B 背景，色帶與提示改用可讀的主題色。
`EditorBlockTheme` 再以實際編輯器底色校正 D／E 對比，不假設編輯器與殼層的深淺相同。
F 的摘要讀取有長度上限，捲動只重新定位，不重新解析 SQL。

## 平台探測結論

- SSMS 實機 `ContentType=SQL`、基底 `code`，`bracehighlight` 有 Fill 與 ZOrder。
- 原生 outlining 轉接器使用 **buffer** aggregator；C 不可只匯出 view tagger。
- Aggregator 會於內容類型變更時 Dispose 並重建 Tagger；A／C／D 各次提供新 Tagger，只共用分析與檢視狀態。
- Marker renderer 優先讀 `BackgroundColor`，所以 B 的自訂色也要套透明度，不能只改 Fill。
- ViewportRelative 只自動處理後續位移；F 初次加入仍須使用 `ViewportTop／Left` 文件座標。
- 不更動 SSMS 自己的大綱或原生導引線開關；其顯示條件可能影響實機結果。

實機狀態與操作步驟見[驗收](block-matching-validation.md)。
實作參考 [Microsoft 的配對 Tagger 範例](https://learn.microsoft.com/en-us/visualstudio/extensibility/walkthrough-displaying-matching-braces?view=visualstudio)。
