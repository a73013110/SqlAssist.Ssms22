# 設定

變更 schema 先讀[設定結構](settings-schema.md)。

設定全部由 **SSMS 22 的 Unified Settings** 提供，沒有自訂設定檔。
按 `Ctrl+,`，或從 **工具 → SqlAssist → 設定…** 直接跳到 SqlAssist 分類。
改完立即生效，不必重開查詢視窗，並跟著 SSMS 的設定漫遊同步。

區塊選項見[區塊配對](block-matching.md)，其餘如下：

| 分類 | 設定 | 預設 |
| --- | --- | --- |
| 一般 | 啟用 SqlAssist | `true` |
| | 輸入時把 T-SQL 關鍵字轉成大寫 | `true` |
| | 自動補上成對的括號與引號 | `true` |
| 建議清單 | 輸入時自動彈出建議清單 | `true` |
| | 只使用 SqlAssist 的建議清單 | `true` |
| | 輸入幾個字元後才彈出清單 | `1` |
| | 在建議清單上方顯示分類篩選列 | `true` |
| | 列出程式碼片段（內建 49 筆與自訂項目） | `true` |
| | 列出資料庫物件與欄位 | `true` |
| 插入與展開 | 插入物件時補上結構描述名稱 | `true` |
| | 插入物件時加上方括號 | `false` |
| | 按 Tab 把 SELECT * 展開成欄位清單 | `true` |
| | SELECT * 展開後的欄位排版 | `oneLineWhenShort` |
| | 在 ALTER 之後展開完整定義 | `true` |
| | 在 INSERT INTO 之後展開完整的欄位與 VALUES | `true` |
| | 在 MERGE INTO 之後展開完整的比對鍵與動作子句 | `true` |
| | 在 EXEC 之後展開完整的參數清單 | `true` |
| | EXEC 展開時包含選擇性參數 | `true` |
| | 選取自訂函式後補上括號 | `true` |
| | 括號裡填入引數預留值 | `false` |
| 物件結構 | 滑鼠停留時顯示物件結構 | `true` |
| | 滑鼠停留時顯示內建名稱的說明 | `true` |
| | 輸入自訂函式的引數時顯示參數提示 | `true` |
| | 建議清單的結構預覽何時展開 | `delay` |
| | 自動展開前的停留毫秒數 | `220` |
| | 預覽視窗的位置 | `stacked` |
| | 預覽視窗的字級 | `14` |
| | 產生指令碼的風格 | `fidelity` |
| | 指令碼包含資料行說明 | `true` |
| | 指令碼附上結構健檢的發現 | `false` |
| | 指令碼附上來源檔頭 | `false` |
| 診斷 | 寫入詳細診斷紀錄 | `false` |

moniker 一律是 `sqlAssist.<分類>.<設定>`，例如
`sqlAssist.suggestions.triggerAfterCharacters`。
註冊檔在 [`src/SqlAssist.Ssms22/SqlAssist.registration.json`](../src/SqlAssist.Ssms22/SqlAssist.registration.json)，
它是設定清單的唯一權威來源；四處對應與守門測試見[設定結構](settings-schema.md#新增一個設定)。

「啟用 SqlAssist」是總開關，關掉之後其他分類的功能全部停止運作，但**設定頁上不會跟著變灰**——
原因見[設定條件式](settings-schema.md#條件式只能參照一個同分類的設定)。「插入與展開」那幾種另外
還需要「建議清單」開著——它們發生在提交建議時，同樣不會變灰。

指令碼那四項同時管 F12 與預覽的指令碼分頁。其餘四十幾個開關不進設定頁，
理由見[指令碼產生](script-generation.md#選項不進-unified-settings)。

「插入物件時加上方括號」管的是**資料庫物件**的名稱。暫存資料表（`#Loan`）與資料表變數
（`@rows`）不在它的管轄內，開著也不會被包起來：`[#Loan]` 雖然合法卻不是任何人會手寫的
樣子，而 `[@rows]` 根本不是合法的 T-SQL，貼進編輯器就是語法錯誤。規則只有一份，在
`Core/Parsing/SqlIdentifier.IsScriptScoped`。

## 四件刻意<b>不</b>是設定的東西

- **清單引擎**：固定使用平台原生管線，舊的自製 WPF 清單已移除。
- **展開萬用字元的行寬**：固定 120 個字元。排法本身是設定，這個分界點不是——
  感覺得到的是「一行還是好幾行」，落在 118 還是 124 沒有人會有意見。
- **清單最多顯示筆數**：模糊比對後的截斷上限固定 300。效能保險而不是偏好——
  清單本來就要捲動，再多打一個字排名也整個重算。
- **預覽視窗的寬高**：拖曳握把記下來的是視窗狀態不是偏好，改存 VS 的
  `WritableSettingsStore`（`SqlAssist\Preview`）。放進 Unified Settings 等於
  每放開一次滑鼠就提交一次設定變更並廣播通知。記法與復原見[預覽視窗](preview-window.md)。

## 設定頁上的按鈕

Unified Settings 的按鈕（`commands`）可以掛在分類上，也可以掛在**單一設定**上。
掛在設定上的按鈕跟著那一項顯示，所以「編輯程式碼片段…」掛在
`sqlAssist.suggestions.includeSnippets`，不是掛在整個「建議清單」分類上。

曾經有一顆「關閉 SSMS 內建的 T-SQL IntelliSense」掛在「建議清單」分類上
（命令 `0x0207`，已移除且不回收）。移掉它的理由值得記下來，因為那是一整類按鈕
的通病：**它改的是別人分類裡的設定，所以設定頁上沒有任何一格會跟著變**——
按鈕按得下去，卻永遠看不出現在是開還是關，只能靠按完跳一個訊息框補救。
`enableOnlyWhen` 也救不了：依 `languages.sql.intelliSense.enableIntellisense`
變灰是跨分類參照，會讓整個「建議清單」頁消失。

改法不是把按鈕做得更好，是**換成一個自己的設定**：
`sqlAssist.suggestions.suppressNativeMemberList` 是我們自己分類裡的布林值，
殼層直接畫成核取方塊、狀態一目了然、也跟著設定漫遊。真正要對外做的那一下
（寫 `LANGPREFERENCES2.fAutoListMembers`）由 `Ssms22/Settings/NativeMemberList`
在套件載入、每次建立 SQL 編輯器與設定變更時各推一次。

由此得到一條通則：**設定頁上的按鈕只適合「做一件事」，不適合「切換一個狀態」。**
留下來的兩顆——「編輯程式碼片段…」與「開啟診斷紀錄檔」——都是前者。

它是唯一一個作用在擴充之外的設定，所以「關於與診斷」同時顯示兩件事：設定要的樣子，
與 SSMS 語言偏好實際的樣子。兩者不一致就是「寫進去了但沒生效」，
那是這個功能唯一會安靜失敗的方式。

**工具 → SqlAssist** 只留下編輯途中會想按的東西：

```text
啟用 SqlAssist ☑            Ctrl+Alt+Shift+S
顯示即時建議 ☑              只關清單，Hover 與關鍵字大寫照常
─────────────
移至定義                     F12          另開查詢視窗顯示可執行的定義
顯示游標處物件的結構         Ctrl+F12     預覽關成 off 時的唯一入口
重新整理建議                 Ctrl+Shift+D  改完資料表結構後用
以片段包住選取範圍           Ctrl+Alt+S   也在查詢視窗右鍵選單
程式碼片段…                  編輯內建與自訂片段
─────────────
設定…                        開啟 Unified Settings 並定位到 sqlAssist
關於與診斷…                  看版本、重要設定、健康檢查；可複製匿名診斷摘要
```

這些鍵是命令表的預設繫結，不是 Unified Settings 的偏好，本擴充也**不自備鍵盤設定
頁**：**工具 → 選項 → 環境 → 鍵盤** 是唯一權威，自製一份只會不同步。命令表給每顆
按鈕都取了 `SqlAssist.` 開頭的正式名稱，在該頁搜尋 `SqlAssist` 就列得出全部命令；
少了那組名稱，殼層只照選單路徑推名字，名稱裡沒有 SqlAssist 就搜不到。

「關於與診斷」分成**概覽／設定摘要／診斷**三頁。複製出的摘要適合直接貼到公開 Issue，
不包含 SQL 文字、伺服器名稱、資料庫名稱或 Windows 使用者名稱；完整診斷紀錄仍可能包含
資料庫物件名稱，附檔前應先檢查內容。
