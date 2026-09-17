# 對話框版面

`DialogWindow` 的內容排版與按鈕規則。視覺語言與互動共通準則見[自製 UI 準則](ui-guidelines.md)。

## 結構

原生 Titlebar 之下由上而下是「資訊列 → 分段 → 頁尾」，元件都在 `SqlAssistChrome.Dialogs`：

| 元件 | 做法 |
|---|---|
| `CreateInfoBar` | 語意圖示＋可換行文字、極淡底色；說明不可復原或受保護的範圍，不另立標題 |
| `CreateSection` | 淡色小標與內容間距 8、分段之間 16，第一段不留上緣；用量分頁共用 |
| `CreateOptionGroup`／`CreateOptionRow` | 一塊表面裝一組選項；整列可點，標題＋一行淡色說明，右側放只屬於該選項的附屬設定 |
| `CreateDialogFooter` | 單一頁尾：左側摘要或狀態吃剩餘寬度，右側動作間距 8、最小寬 80，主要動作放最後 |
| `CreateDangerButton` | 主要動作本身是破壞性時使用：語意色淡底與配對前景，按鈕寫出動作與數量 |

確認框（`CreateConfirmationContent`）與清除紀錄已套用；其他對話框改版時跟進，不在呼叫端另排頁尾。

## 按鈕與確認

- 設定唯一 `IsDefault` 與 `IsCancel`；危險動作不能成為預設，也不能只靠紅色警示。
- 片段確認框共用 `SqlAssistConfirmationWindow`：按鈕明寫刪除／停用／還原預設，取消為
  預設與初始焦點；影響說明交代儲存後才寫檔。一般成功回饋留在狀態列，失敗沿用原生訊息框。
- 對話框本身已列出影響範圍、數量寫在按鈕上、條件一改就停用到重新計算完成時，不再疊第二層確認框。
