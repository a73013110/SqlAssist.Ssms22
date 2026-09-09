# 包夾清單的按鍵攔截

本頁只講[片段包夾](snippet-surround.md)清單開著時，按鍵為什麼會改到後面那份 SQL。

## 殼層先把按鍵解析成命令

**殼層是照「作用中的視窗框架」預先把按鍵解析成命令的**，清單開著時那個框架仍然是
查詢視窗——這件事**與焦點落在哪個 HWND 無關**。Tab、↑↓、Enter、Delete、Backspace
在「文字編輯器」範圍全都有繫結，於是變成編輯器命令沿著查詢視窗的命令鏈派送，直接
改到 SQL；沒有繫結的英數字不會變成命令，才會只有打字看起來正常。換成 `Popup`、
獨立視窗或任何自畫的東西都一樣，**改視窗殼層修不掉這個問題**。

攔截點因此在 `SqlShellCommandFilter`（插在查詢視窗命令鏈最前面）：清單開著時把這些
命令交回 `SqlSnippetSurroundPicker`，對照表在 `SqlSnippetSurroundKeys`。

- 對照的是**按鍵**不是行為：命令換回 `Key` 重新丟進 WPF 的輸入管線，修飾鍵仍是實體
  狀態，所以 Shift+Tab（`BACKTAB`）、Shift+↑（`*_EXT`）、Ctrl+←（`WORDPREV`）都對回
  同一個方向鍵，文字方塊與清單自己處理，不在這裡重寫一份鍵盤語意。
- **通道與冒泡兩個階段都要發**：`InputManager.ProcessInput` 推一個 `KeyEventArgs`
  只會發那一個事件。只發 `KeyDown` 時文字方塊的編輯鍵正常（那是 `KeyDown` 的類別
  處理），但清單掛在 `PreviewKeyDown` 的 ↑↓／Enter／Esc 收不到——症狀是搜尋框裡那
  三個鍵沒反應、↑↓ 要先點進清單才有用。
- 剪貼簿與復原（`Copy`／`Cut`／`Paste`／`SelectAll`／`Undo`／`Redo`）直接執行對應的
  `ApplicationCommands`，Ctrl+C 才會複製預覽而不是編輯器裡的選取。
- `QueryStatus` 也要認領：編輯器把某個命令回報成停用時（例如沒東西可復原），殼層
  連 `Exec` 都不會派送，那個鍵會安靜地消失。
- 對照不到的命令不攔，往下轉給 SSMS。對照得到卻發現焦點已經被搶回編輯器時，先把
  焦點要回來並**吞掉**那一鍵：往下轉就是改到 SQL，少一次按鍵只要再按一次。
  清單沒開時紀錄檔仍會出現 `未處理的殼層命令：VSStd97/SelectAll(31)` 這類行——
  那是每個命令只記第一次的診斷，與包夾無關。
- 熱路徑代價是每個按鍵多一次靜態欄位讀取（有沒有清單開著），比一次 GUID 比對便宜。

## Esc 還有第二條路

實測**第一次 Esc 不一定會變成 `VSStd2K/CANCEL` 走進命令鏈**：查詢視窗會先拿它取消
自己的選取，症狀是要按兩次才關得掉清單。現代管線的 `EscapeKeyCommandArgs` 收得到
那一次，所以 `SqlAssistCompletionCommandHandler` 也接一條，並且排在建議清單與結構
預覽前面。兩條路都呼叫同一個 `Close`，重複進來由 `_closed` 擋掉。

## 內容留在 Popup，不換成獨立視窗

**實測過改成 `DialogWindow` 沒有用，而且更糟**：紀錄檔裡 `UP`／`DOWN`／`RETURN`／
`CANCEL` 照樣抵達查詢視窗的命令鏈，而那個視窗連焦點都拿不到——殼層在命令結束後把
焦點還給文件，於是連打字都掉回編輯器並觸發建議清單，Esc 也關不掉。`Popup` 拿得到
焦點，外觀也貼著編輯器，所以留著；要調整大小改用右下角的握把
（`SqlAssistChrome.CreateResizeGrip`），縮放只改內容尺寸，落點仍貼著選取範圍。
