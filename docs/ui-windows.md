# 視窗骨架

停靠工具窗（SQL Memory、SQL Search）與之後的視窗共用的骨架與主從區契約。清單列見
[清單列](ui-rows.md)，視覺語言見 [UI 準則](ui-guidelines.md)，元件的唯一出處見
[平台共用元件](shared-components-platform.md)。

## 骨架

視窗從同一個入口組裝，三塊固定：

- **工具列** `Dock=Top`：1–2 層。第一層搜尋框吃滿剩餘寬度，第二層放 filters 與分段開關，
  窄版依群組換行。工具窗沒有原生 Titlebar，第一列直接是工具列。
- **已選條件列**：非預設時才出現（否則 `Collapsed`），而且**永遠只有一列**。chip 一個
  **維度**一顆：十字清掉整個維度，本體開那個維度的面板，值的名單留在面板與摘要。一值一顆的
  那一版勾八個資料庫就長成三列，等於少看六筆結果。放不下橫向捲動，不換行
  （`CreateHorizontalStrip`，與 Preview 資訊列同一份）。
- **主從區**：取剩餘空間。
- **狀態列** `Dock=Bottom`：平時 `Collapsed`，只回報結果數、部分結果與失敗。

## 主從區與 Preview 標頭

寬到門檻轉左右分割，否則上下。轉向在 `MeasureOverride` 決定，等到排版才換會閃一次舊版面；
門檻留 24–40 DIP hysteresis，臨界寬度不得反覆跳。兩個方向的拖曳比例分開記，換向再換回來
仍是使用者拖過的那一份。Resize 不重查資料、不重建結果集合，不改選取與捲動位置。

兩個方向的**預設**相反：上下是清單 3：Preview 2，左右是 2:3。清單列的寬度有上界（名稱截在
`RowNameMaxWidth`，其餘固定寬或可省略），再寬只是右邊空著；SQL 沒有，窄一點就是每一行都折。

Preview 標頭（開關與摘要）分兩態：

- 展開時標頭屬於 Preview，貼在它上緣：上下分割橫跨整列（那裡本來就在清單與 Preview 之間），
  左右分割**只屬於右邊那一欄**，不跨到清單上方——管右邊那一塊的開關出現在清單左上角，
  就是它不該在的位置。也不能放進中間那欄，那裡只有 5 DIP 寬。清單與分隔線跨過標頭那一列。
- 收合時 detail 與 splitter `Collapsed`，標頭退化成貼在清單下緣（上下分割）或右緣
  （左右分割）的單列把手。開關不得跟著 detail 一起收掉，否則收合後再也展不開。

兩態都要有 `AutomationName`，並隨狀態換成「收合預覽／展開預覽」。

## 狀態表面

載入、空、錯誤、無權限四種狀態只有一份實作，疊在同一塊內容上，不各占一塊版面。沒有選取時 Preview 的
摘要與操作一起收起：膠囊樣板繫不到值仍會把底畫出來，那塊沒有字的灰方塊看起來像是一筆載不出名稱
的結果。錯誤與無權限
走同一個出口，文案分成「這一輪讀不到」與「權限不足」。一列都沒有時頁尾與狀態列讓開，同一句話
不在畫面中央與下緣各說一次；清單上還留著列時失敗留在狀態列，不遮住讀得到的那幾筆。

兩個抬頭的下一步完全不同（重試或換條件 vs. 去要權限），所以「權限不足」是一句**斷言**，
換抬頭的門檻只有一條：讀不到的來源**每一個**都回報得出結構化的權限原因。其中之一就換的話，
使用者照抬頭去要了權限，那個連不上的來源下一輪還是讀不到，而畫面上看不出他要錯了東西；
反過來一律說「這一輪讀不到」的那一版，則會讓真的缺權限的人一直重試。SQL Search 那一邊
由 `SearchUnavailableKind` 帶著走，見 [SQL Search](search.md#掃描預算與部分結果)。
說明那一段一律原樣來自來源，這一層不寫文案。

## design token

字型、字級、間距、圓角、動畫長度與語意色只進 `SqlAssistChrome`（含 partial）與
`ThemePalette`／`ThemeColorMath`，顏色繫結 `VsThemeBrushes` 的動態資源。不得另開
`ResourceDictionary`，不得在功能目錄複製樣板或硬寫 RGB。

## 動畫與延遲

| 分級 | 資產 | 長度 |
|---|---|---|
| 內容表面出現 | `PlayAppear` | 120 ms 淡入 |
| 列的揭露 | `MemoryCardEnterDuration`／`MemoryCardExitDuration` | 180／140 ms |
| 狀態回饋 | `UsageBadgePop`、`SearchStatusPop` | 240 ms |
| 列操作的揭露 | `RowActionRevealDuration` | 120 ms 淡入＋6 DIP 滑入 |
| 展開／收合箭頭轉向 | `ChevronTurnDuration` | 140 ms 轉到位，可中途反向 |
| 主從區轉向 | 無，也不要加 | 轉向在 `MeasureOverride`，加動畫會抖 |

三級都受[全域動畫設定](settings.md)控制。debounce 走同一張共用常數表：Search 搜尋 200、
預覽 220、Cleanup 估算 250、Memory 搜尋 300 ms。

## 元件邊界

- Badge、可移除 Chip、按鈕型 Chip 是不同 primitive，共用尺寸、圓角、spacing、狀態色與
  icon slot，不共用互動語意。
- 篩選列、兩級分隔線、過濾面板與展開箭頭見[篩選](ui-filters.md)。
- 兩個 Browser 不合成通用元件：外觀共用，領域語意與 command 留在各 feature。

## 驗收

保留取消／generation guard、背景工作、每批 40 筆與 recycling virtualization。禁用每列陰影、
模糊與複雜動畫；主題／DPI 切換後不殘留舊 brush 或裁切 icon。測試涵蓋寬窄切換與 hysteresis
臨界穩定、收合態仍展得開、第一列順序、缺值 collapse、片段列只在本文命中出現、單／多選
filter、面板第一列的預設與續頁／排序、兩級分隔線（群距大於群內距、列首／換行／整群收起時孤線
收起）、沒有選取時 Preview 摘要與操作收起、箭頭兩個方向
與動畫關掉時直接寫角度、操作層只有左緣淡出、選取與捲動保存、四種狀態表面（「這一輪讀不到」與
「權限不足」兩種抬頭各一條，含只有一部分來源說得出權限時抬頭不換）、Light／Dark／Blue 與 100／150／200% DPI。展開態的
Preview 開關屬於右欄、300 DIP 下每一組都還在列內、窄版降級與 overflow 也要測。
