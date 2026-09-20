# 視窗骨架與清單列

停靠工具窗（SQL Memory、SQL Search）與之後的視窗共用的骨架、主從區與清單列契約。
視覺語言見 [UI 準則](ui-guidelines.md)，元件的唯一出處見[平台共用元件](shared-components-platform.md)。

## 骨架

視窗從同一個入口組裝，三塊固定：

- **工具列** `Dock=Top`：1–2 層。第一層搜尋框吃滿剩餘寬度，第二層放 filters 與分段開關，
  窄版依群組換行。工具窗沒有原生 Titlebar，第一列直接是工具列。
- **主從區**：取剩餘空間。
- **狀態列** `Dock=Bottom`：平時 `Collapsed`，只回報結果數、部分結果與失敗。

## 主從區與 Preview 標頭

寬到門檻轉左右分割，否則上下。轉向在 `MeasureOverride` 決定，等到排版才換會閃一次舊版面；
門檻留 24–40 DIP hysteresis，臨界寬度不得反覆跳。兩個方向的拖曳比例分開記，換向再換回來
仍是使用者拖過的那一份。Resize 不重查資料、不重建結果集合，不改選取與捲動位置。

Preview 標頭（開關與摘要）分兩態：

- 展開時標頭屬於 Preview，橫跨整個主從區寬度。左右分割時留在 divider 列並 span 全欄——
  放進中間那欄會被擠進 5 DIP 寬的分隔欄。
- 收合時 detail 與 splitter `Collapsed`，標頭退化成貼在清單下緣（上下分割）或右緣
  （左右分割）的單列把手。開關不得跟著 detail 一起收掉，否則收合後再也展不開。

兩態都要有 `AutomationName`，並隨狀態換成「收合預覽／展開預覽」。

## 清單列

第一列語意固定，內容列 1–2 列，程式碼片段是可選的第三列。

第一列：`主要名稱 → 狀態／物件類型 → 命中部位／次要標記 → 彈性空白 → 伺服器 → 資料庫 → 時間`。

工具窗停在右側時可用寬度約 300 DIP，名稱是使用者唯一要掃的東西，固定在最左，膠囊不得排在
它前面；這個順序就是 UI 準則的「識別資訊 → 性質／狀態 → 統計或範圍」。

- Memory：`檔名 → 草稿／執行／收藏 → 次數（可選）→ 伺服器 → 資料庫 → 時間`。
- Search：`物件名稱 → 物件類型 → 命中部位 → 伺服器 → 資料庫`；類型不能只藏在 tooltip，
  沒有真實更新時間就不顯示時間。
- 狀態與連線用小型 badge，各帶一個 16 DIP `SqlIcon`、間距 4–5 DIP；語意已由膠囊圖示表達
  就不重複第二個。主要名稱維持一般文字。
- 缺值直接 collapse 不留空槽；名稱吃剩餘寬度並 ellipsis，全文由 tooltip／Preview 提供。

內容列放 Memory 的單行 SQL 摘要，或 Search 的完整限定名稱、欄位路徑與脈絡膠囊。片段列只在
本文命中時出現，等寬字、單行、保留高亮；名稱與資料行命中的片段就是名稱本體，不再畫一次。

- 操作放最右，其餘收進 overflow；動作區用 `Visibility.Hidden` 預留寬度，hover 不跳版面。
- 超窄時先縮短文字與 badge，再把低優先 badge 改 icon-only；不換列，內容列不得被動作擠掉。

## 狀態表面

載入、空、錯誤、無權限四種狀態只有一份實作，疊在同一塊內容上，不各占一塊版面。錯誤與無權限
走同一個出口，文案分成「這一輪讀不到」與「權限不足」。

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
| 主從區轉向 | 無，也不要加 | 轉向在 `MeasureOverride`，加動畫會抖 |

三級都受[全域動畫設定](settings.md)控制。debounce 走同一張共用常數表：Search 搜尋 200、
預覽 220、Cleanup 估算 250、Memory 搜尋 300 ms。

## 元件邊界

- Badge、可移除 Chip、按鈕型 Chip 是不同 primitive，共用尺寸、圓角、spacing、狀態色與
  icon slot，不共用互動語意。
- Filter Flyout 支援 `Single`／`Multiple`／`SearchableMultiple`；伺服器單選用 radio、自動
  關閉、不顯示全選／清除；分類保留 provider group 與 sort order。選項清單是虛擬化
  `ItemsControl`，不用 `ScrollViewer + StackPanel` 承載大量選項；快取 Style／ControlTemplate，
  不快取有 parent 的 `UIElement`。
- 兩個 Browser 不合成通用元件：外觀共用，領域語意與 command 留在各 feature。

## 驗收

保留取消／generation guard、背景工作、每批 40 筆與 recycling virtualization。禁用每列陰影、
模糊與複雜動畫；主題／DPI 切換後不殘留舊 brush 或裁切 icon。測試涵蓋寬窄切換與 hysteresis
臨界穩定、收合態仍展得開、第一列順序、缺值 collapse、片段列只在本文命中出現、單／多選
filter、選取與捲動保存、四種狀態表面、Light／Dark／Blue 與 100／150／200% DPI。
