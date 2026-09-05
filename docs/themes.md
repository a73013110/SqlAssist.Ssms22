# SSMS 佈景主題連動

介面跟隨 SSMS，不讀 Windows 的深淺模式或自行保存 `isDark`。Windows 系統色只用於
高對比與最後備援。自訂編輯器配色屬於另一個範圍，不能拿 SQL 前景搭配 Tooltip 底色。

## 唯一來源與生命週期

- `UI/VsThemeBrushes` 只訂閱一次 `VSColorTheme.ThemeChanged`，並接收高對比變更。
  原生資源字典優先，尚未併入時向殼層色彩服務查詢；前景或背景缺失就整組備援。
- `UI/ThemeResourceSet` 保存可共用的動態資源。控制項用 `WithTheme`／
  `SetResourceReference`，樣板用 `SetResourceReference`，觸發器用其 `Setter` 工廠。
  不把解析後的 Brush 寫死在控制項或樣板裡。
- 每個自製視窗根節點及獨立 `ContextMenu` 呼叫 `VsThemeBrushes.Apply`。字典只包含
  資源、不保存視窗；不得改動 `Application.Resources` 或 SSMS 全域設定。
- 捲軸、下拉清單、右鍵選單沿用 SSMS 的完整原生樣式。局部系統鍵別名涵蓋舊樣板的
  角落填色、選取與前景；不只替外層 Border 換色。
- 衍生筆刷每輪更新只建立一次並凍結；相同顏色保留原物件，避免多餘失效通知。
  高對比使用完整選取色及配對文字色，不沿用低透明度選取底色。
- `SqlAssistPackage.Dispose` 解除殼層與系統訂閱。視窗本身透過動態資源更新，
  不需要每個視窗各自訂閱全域主題事件。

## SQL 指令碼

`Preview/SqlScriptTheme` 於第一次開啟指令碼分頁時才建立，使用目前查詢視窗的
`IClassificationFormatMap` 及 `IWpfTextView.Background`，不是通用 `"text"` 分類。
分類配色、編輯器底色及主題通知皆會使外觀失效；分頁不可見時延後到顯示前更新。

`SqlScriptDocument` 的每個 Run 保存分類資源鍵。換主題只替換筆刷與字型資源，
不重新詞法分析、不重建 FlowDocument、不重查資料庫，既有文字選取及捲動狀態得以保留。
更改字型可能自然引起重新排版，不能保證換字級後仍有相同像素落點。

`ThemeRefreshQueue` 合併同一輪連續通知，回 UI 執行緒更新；平台 callback 仍須以
`SqlAssistPlatformGuard.Probe` 包覆。查詢視窗關閉時釋放 `SqlScriptTheme`，解除所有
訂閱並取消尚未派送的更新，避免全域事件保留編輯器。

## 驗證

`SqlAssist.Ssms22.Tests` 在 net48 STA 執行產品的純 WPF 實作，不需啟動 SSMS。
涵蓋雙向換色、Run 與選取保留、樣板與選取配對、筆刷共用、局部系統鍵及通知合併。
共用控制項的多 DPI 渲染輸出位於被忽略的 `artifacts/theme-qa/`；這些是測試配色，
不是 SSMS 實機截圖，也不能取代原生 Popup 的整合驗收。

SSMS 手動驗收：

1. 同一查詢視窗切換淺色 → 深色 → 淺色，分別在預覽顯示中及隱藏後重開驗證。
2. 驗證所有分頁、載入／錯誤狀態、右鍵選單、捲軸與握把，不應出現新舊主題混色。
3. 檢查片段管理員、診斷、欄位剖析與完整儲存格內容；Windows 與 SSMS 設相反主題。
4. 更改 SQL 字型、字級及分類色；保留選取與捲動、確認沒有額外中繼資料查詢。
5. 高對比、100%／150%／200% DPI、最小尺寸、長字串與鍵盤焦點均需驗證。
6. 多個查詢視窗連續切換主題再關閉，確認沒有延後更新錯誤或事件造成的視窗滯留。

平台依據：[VS 色彩服務](https://learn.microsoft.com/en-us/visualstudio/extensibility/ux-guidelines/colors-and-styling-for-visual-studio?view=vs-2022)、
[編輯器分類外觀](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.text.classification.iclassificationformatmap?view=visualstudiosdk-2022)。
