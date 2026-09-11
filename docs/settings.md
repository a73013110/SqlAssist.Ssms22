# 設定

變更 schema 先讀[設定結構](settings-schema.md)。

設定全部由 **SSMS 22 的 Unified Settings** 提供，沒有自訂設定檔。
按 `Ctrl+,`，或從 **工具 → SqlAssist → 設定…** 直接跳到 SqlAssist 分類。
改完立即生效，不必重開查詢視窗，並跟著 SSMS 的設定漫遊同步。

區塊選項見[區塊配對](block-matching.md)，「通知與背景工作」一整頁見[通知呈現與驗證](notifications-ui.md#呈現與設定)，
其餘如下：

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
`sqlAssist.suggestions.triggerAfterCharacters`。「通知與背景工作」那一頁是
`sqlAssist.notifications.*`，其中十一個種類開關不寫成 moniker 常數與屬性，改由
`NotificationKindToggle.All` 那張表驅動，新增一個種類只動註冊檔與那張表。
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

設定頁上的按鈕、**工具 → SqlAssist** 選單與「關於與診斷」見[設定入口](settings-entries.md)。
