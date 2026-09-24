# Ctrl＋點擊導覽

本頁包含查詢視窗裡用滑鼠點物件名稱的手勢；點下去之後的預覽見[結構預覽](structure-preview.md)，
定義見[移至定義](go-to-definition.md)。

| 手勢 | 等於 |
|---|---|
| 按住 `Ctrl`，滑鼠移到物件名稱上 | 名稱加底線、游標變手指 |
| `Ctrl`＋點擊 | 在那裡按 `Ctrl+F12`：浮動結構預覽；內建函式開完整說明 |
| `Ctrl+Shift`＋點擊 | 在那裡按 `F12`：定義開進新的查詢視窗 |

由「物件結構」頁的 `sqlAssist.structure.clickNavigation` 整組開關，關掉後回到編輯器原本的
Ctrl＋點擊「選取整個單字」。修飾鍵不給選：Alt＋拖曳是方塊選取、Ctrl+Alt＋點擊是多重游標，
能換的只剩更糟的組合。

## 誰算得上是連結

判斷只看滑鼠所在的那一行、不查中繼資料（`SqlClickTarget`）：它在滑鼠移動路徑上，而名稱
認不認得由點下去之後的背景解析回答。錯在寬的一側是多一句狀態列訊息，錯在窄的一側是
底線只出現在快取剛好載過的名稱上。

只擋一定不是物件的兩種：沒加括號的保留字，以及內建名稱——後者結構預覽答得出（完整說明，
規則同 `SqlBuiltInDoc.DeservesWindow`），定義答不出，所以 `Ctrl+Shift` 時不加底線。
非保留的關鍵字不擋，`Type`、`Status` 這類欄位名很常見。

## 不搶走的操作

- **按下就接手、放開才執行。** 不吞掉按下的話，編輯器會先選起整個單字、搬動游標；放開才
  執行，按下後拖開就能反悔。吞掉按下也吞掉了取得焦點，所以接手時自己把焦點給編輯器。
- **修飾鍵必須完全相同**（`SqlClickGestures`）；「包含 Ctrl 就算」會搶走多重游標。新增手勢只在
  那張表加一列，動作接在 `SqlObjectNavigation.Run`。
- **點在既有選取範圍裡不接手**：那是 Ctrl＋拖曳複製。雙擊也不接手。
- 滑鼠處理器排在 `WordSelection`、`GoToDefMouseHandler`、`DragDropMouseProcessor` 之前
  （名稱取自 SSMS 22 的 `Microsoft.VisualStudio.Platform.VSEditor.dll`）。

快捷鍵、選單與點擊都走 `SqlObjectNavigation`，只差位置是游標還是滑鼠。狀態列訊息因此寫出
名稱而不寫「游標處」——點擊時游標根本沒動。

## 呈現

底線是分類標記（`SqlClickLinkFormat`），只加底線不換色，字色與底線都跟著原本的分類與佈景走。
手指游標還原的是編輯器原本的**本地值**：寫死 IBeam 會蓋掉邊欄與捲軸的箭頭。

滑鼠事件帶不到鍵盤裝置，修飾鍵由可替換的 `ModifierSource` 讀（與清單多選同一個例外，
見 `KeyboardDeviceUsageTests`）；只按放修飾鍵而滑鼠不動時，由按鍵處理器帶
`KeyEventArgs.KeyboardDevice` 進來。版面變更（捲動、打字、縮放）重算一次，失去聚合焦點時收掉——
切到別的程式時放開 Ctrl 收不到按鍵事件。

## 尚未確認

- 實機：SSMS 22 的 `GoToDefMouseHandler` 對 SQL 編輯器是否真的沒有接上。
- 實機：摺疊區塊之後、以及高 DPI 下，底線與點擊位置是否對齊。
