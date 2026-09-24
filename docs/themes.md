# SSMS 佈景主題連動

本頁包含殼層與 SQL 指令碼的取色、生命週期與驗收；視覺準則見[UI 準則](ui-guidelines.md)。
介面跟隨 SSMS，不讀 Windows 的深淺模式或自行保存 `isDark`。Windows 系統色只用於
高對比與最後備援。自訂編輯器配色屬於另一個範圍，不能拿 SQL 前景搭配 Tooltip 底色。

## 彩色主題取色

SSMS 新彩色主題使用 Fluent `ShellColors`；舊 `EnvironmentColors.ToolTip` 等鍵仍可能
只有中性灰，不能以「有收到換主題事件」當成已跟隨色系。

- 預覽抬頭／頁尾與對話框外層用 `SolidBackgroundFillTertiary`，內容表面用
  `SolidBackgroundFillQuaternary`，文字分別取 `TextFillPrimary`／`TextFillSecondary`。
- 焦點、主索引鍵徽章、主要按鈕與選取使用 `AccentFillDefault` 推導淡色回饋；
  `ThemePalette` 在視窗與內容兩種底色上檢查對比，必要時減淡而不換成固定灰。
- 取公開的內容語意色，不挪用 `ShellInternal` 的主標題列裝飾色；因此同色系不代表
  所有區域都塗成主標題列的飽和色。SQL 區域仍使用下方的編輯器分類配色。
- 缺少 Fluent 前景或背景時整組降級到既有殼層／系統備援，不能混用新底色與舊文字。

## 唯一來源與生命週期

- `UI/VsThemeBrushes` 只訂閱一次 `VSColorTheme.ThemeChanged`，並接收高對比變更。
  原生資源字典優先，尚未併入時向殼層色彩服務查詢；前景或背景缺失就整組備援。
- `UI/ThemeResourceSet` 保存可共用的動態資源。控制項用 `WithTheme`／
  `SetResourceReference`，樣板用 `SetResourceReference`，觸發器用其 `Setter` 工廠。
  不把解析後的 Brush 寫死在控制項或樣板裡。
- 每個自製視窗根節點及獨立 `ContextMenu` 呼叫 `VsThemeBrushes.Apply`。字典只包含
  資源、不保存視窗；不得改動 `Application.Resources` 或 SSMS 全域設定。
- 捲軸、下拉清單、右鍵選單沿用 SSMS 的完整原生樣式；覆蓋式捲軸例外見
  [UI 準則](ui-guidelines.md)。局部系統鍵別名涵蓋舊樣板的
  角落填色、選取與前景；不只替外層 Border 換色。
- 衍生筆刷每輪更新只建立一次並凍結；相同顏色保留原物件，避免多餘失效通知。
  高對比使用完整選取色及配對文字色，不沿用低透明度選取底色。
- `SqlAssistPackage.Dispose` 解除殼層與系統訂閱。視窗本身透過動態資源更新，
  不需要每個視窗各自訂閱全域主題事件。

## SQL 指令碼

`Preview/SqlScriptTheme` 於第一次開啟指令碼分頁時才建立。**有查詢視窗時**使用那一個視窗的
`IClassificationFormatMap` 及 `IWpfTextView.Background`，不是通用 `"text"` 分類——同一份設定在
不同檢視上可以套不同的外觀類別，拿通用那一份會讓預覽與旁邊的查詢視窗顏色對不上。
分類配色、編輯器底色及主題通知皆會使外觀失效；分頁不可見時延後到顯示前更新。

**一個查詢視窗都沒有**（只連了資料庫）時退回 `"text"` 這個外觀類別。兩條路要到的是同一份
Fonts and Colors 設定，所以之後打開查詢視窗不會換一套顏色。

字型、字級、底色與前景全部跟著那一份設定，**不以「有沒有檢視」當條件**。以檢視存在與否分岔的
那一版在沒有查詢視窗時改用自己的字級，症狀是同一份 SQL 在開查詢視窗前後大小會變。
問不到時才退回 `SqlAssistChrome.CodeFont` 與 `DefaultMetrics.Body`——同一組值也是唯讀檢視
建立時套的那一組，因此文件建好前後不會跳動；不另寫只有這裡看得到的字級常數。

底色缺檢視時改問 `IEditorFormatMap` 的 **Plain Text** 那一格——編輯器的底色畫在檢視上而不在文字上，
`DefaultTextProperties.BackgroundBrush` 沒有檢視時是空的。底色與前景**成對**採用
（`UI/ScriptPalette.Surface`），缺一個或它自己就讀不到時整組退回工具窗那一組，不混用兩邊。

分類色對比不足時**朝可讀的方向調整、保留色相**（`UI/ScriptPalette.Classification`），只有真的問不到
顏色才退回前景色。換成前景色的那一版讓 `keyword`／`comment`／`string`／`number` 全部相同，
症狀是 SQL Memory 與 SQL Search 的預覽整份同一個顏色，而使用者會以為高亮壞了。觸發條件是
**佈景主題與編輯器外觀分屬兩個設定**：「編輯器外觀 = 比對佈景主題」配上彩色深色主題（月光、
神秘森林、辣紅）時，工具窗底色與編輯器底色不同深淺，借來的分類色過不了 4.5:1。

「編輯器外觀」改動走的是 `IEditorFormatMap.FormatMappingChanged`，只有 Plain Text 那一格算數；
本擴充自己回寫的 marker 格式不是配色輸入，不重算整輪色票。

服務也要跟著換一條路拿。`SqlPreviewServices.Current` 是由**編輯器建立接聽器**登記的，
沒有開過查詢視窗時它從頭到尾是 null；`SqlPreviewServices.Resolve()` 改向殼層的 MEF 容器
（`SComponentModel`）要同一組服務並登記起來。要的是**那一個**容器裡的服務，不是自己 new 一份
MEF host——後者拿到的是對不上編輯器設定的第二份外觀。取不到就回 null，呼叫端退回自己的前景色；
著色讀不到還畫得出 SQL，整個預覽開不起來就不行。這條路每次呼叫都會重試，所以包在 `Probe` 裡，
連續失敗不會灌爆紀錄檔。

`SqlScriptDocument` 的每個 Run 保存分類資源鍵。換主題只替換筆刷與字型資源，
不重新詞法分析、不重建 FlowDocument、不重查資料庫，既有文字選取及捲動狀態得以保留。
更改字型可能自然引起重新排版，不能保證換字級後仍有相同像素落點。

`ThemeRefreshQueue` 合併同一輪連續通知，回 UI 執行緒更新；平台 callback 仍須以
`SqlAssistPlatformGuard.Probe` 包覆。查詢視窗關閉時釋放 `SqlScriptTheme`，解除所有
訂閱並取消尚未派送的更新，避免全域事件保留編輯器。

## 驗證

`SqlAssist.Ssms22.Tests` 在 net48 STA 執行產品的純 WPF 實作，不需啟動 SSMS。多 DPI 渲染輸出位於
被忽略的 `artifacts/theme-qa/`，不是 SSMS 實機截圖，也不能取代原生 Popup 的整合驗收。

SSMS 手動驗收：

1. 同一查詢視窗切換淺色 → 深色 → 淺色，分別在預覽顯示中及隱藏後重開驗證。
   另測 Mango Paradise ↔ Cool Breeze、Juicy Plum ↔ Mystical Forest，不能只測亮暗。
2. 驗證所有分頁、載入／錯誤狀態、右鍵選單、捲軸與握把，不應出現新舊主題混色。
3. 檢查片段管理員、診斷、欄位剖析與完整儲存格內容；Windows 與 SSMS 設相反主題。
4. 更改 SQL 字型、字級及分類色；保留選取與捲動、確認沒有額外中繼資料查詢。
   另在**連了資料庫但一個查詢視窗都沒開**時，把「編輯器外觀」在比對佈景主題與明確配色之間切換，
   確認預覽當場換色且四種分類分得開——這一段只有在沒有查詢視窗時才走得到。
5. 同樣在沒有查詢視窗時看一次 SQL Memory 與 SQL Search 的預覽，再開一個查詢視窗：字型、字級與
   底色都不應該在那一刻改變。
6. 高對比、100%／150%／200% DPI、最小尺寸、長字串與鍵盤焦點均需驗證。
7. 多個查詢視窗連續切換主題再關閉，確認沒有延後更新錯誤或事件造成的視窗滯留。

平台依據：[VS 色彩服務](https://learn.microsoft.com/en-us/visualstudio/extensibility/ux-guidelines/colors-and-styling-for-visual-studio?view=vs-2022)、
[Fluent 主題遷移](https://learn.microsoft.com/en-us/visualstudio/extensibility/migration/modernize-theme-colors?view=visualstudio)、
[語意色用途](https://learn.microsoft.com/en-us/visualstudio/extensibility/ux-guidelines/theme-color-token-reference?view=visualstudio)、
[編輯器分類外觀](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.text.classification.iclassificationformatmap?view=visualstudiosdk-2022)。
