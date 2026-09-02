# 設定

設定全部由 **SSMS 22 的 Unified Settings** 提供，沒有自訂設定檔。
按 `Ctrl+,` 開啟設定視窗，或從 **工具 → SqlAssist → 設定…** 直接跳到 SqlAssist 分類。
改完立即生效，不必重開查詢視窗；設定會跟著 SSMS 的設定漫遊同步。

| 分類 | 設定 | 預設 |
| --- | --- | --- |
| 一般 | 啟用 SqlAssist | `true` |
| | 輸入時把 T-SQL 關鍵字轉成大寫 | `true` |
| | 按 Tab 把 SELECT * 展開成欄位清單 | `true` |
| | SELECT * 展開後的欄位排版 | `oneLineWhenShort` |
| 建議清單 | 輸入時自動彈出建議清單 | `true` |
| | 只使用 SqlAssist 的建議清單 | `true` |
| | 輸入幾個字元後才彈出清單 | `1` |
| | 列出程式碼片段（內建 45 筆與自訂項目） | `true` |
| | 列出資料庫物件與欄位 | `true` |
| | 在建議清單上方顯示分類篩選列 | `true` |
| | 插入物件時補上結構描述名稱 | `true` |
| | 插入物件時加上方括號 | `false` |
| | 在 INSERT INTO 之後展開完整的欄位與 VALUES | `true` |
| | 在 MERGE INTO 之後展開完整的比對鍵與動作子句 | `true` |
| | 在 EXEC 之後展開完整的參數清單 | `true` |
| 物件結構 | 滑鼠停留時顯示物件結構 | `true` |
| | 建議清單的結構預覽何時展開 | `delay` |
| | 自動展開前的停留毫秒數 | `220` |
| | 預覽視窗的位置 | `stacked` |
| | 預覽視窗的字級 | `14` |
| 診斷 | 寫入詳細診斷紀錄 | `false` |

moniker 一律是 `sqlAssist.<分類>.<設定>`，例如
`sqlAssist.suggestions.triggerAfterCharacters`。
註冊檔在 [`src/SqlAssist.Ssms22/SqlAssist.registration.json`](../src/SqlAssist.Ssms22/SqlAssist.registration.json)，
它是「有哪些設定」的唯一權威來源：`SqlAssistSettingsReaderTests` 以它為基準反推程式碼，
檢查每一個 moniker 都有常數、都被讀進 `SqlAssistSettings` 的某個屬性、預設值兩邊一致，
而且都在變更訂閱清單裡。漏掉任何一步都是建置失敗，不會變成執行期的安靜回退。

「啟用 SqlAssist」是總開關，關掉之後其餘各頁的功能全部停止運作，但**設定頁上不會跟著變灰**——
原因見下面的〈條件式只能參照一個同分類的設定〉。

「插入物件時加上方括號」管的是**資料庫物件**的名稱。暫存資料表（`#Loan`）與資料表變數
（`@rows`）不在它的管轄內，開著也不會被包起來：`[#Loan]` 雖然合法卻不是任何人會手寫的
樣子，而 `[@rows]` 根本不是合法的 T-SQL，貼進編輯器就是語法錯誤。規則只有一份，在
`Metadata/Formatting/SqlIdentifier.IsScriptScoped`。

## 新增一個設定

要動四處，順序無所謂，但**四處都要**：

| # | 檔案 | 加什麼 |
|---|---|---|
| 1 | `src/SqlAssist.Ssms22/SqlAssist.registration.json` | 型別、`default`、`title`、`description`、`order`；數值加 `minimum`／`maximum`，列舉加 `enum` 與 `enumItemLabels` |
| 2 | `Core/Settings/SqlAssistSettings.cs` | 強型別屬性，預設值必須等於註冊檔的 `default` |
| 3 | `Core/Settings/SqlAssistMonikers.cs` | 一個 `const string`。訂閱清單 `All` 由反射產生，不必手動加 |
| 4 | `Core/Settings/SqlAssistSettingsReader.cs` | `Read()` 裡的一行對應；列舉要加解析、數值要套 `SqlAssistLimits` 的收斂 |

漏掉 2、3、4 任何一處都是**建置失敗**，因為 `SqlAssistSettingsReaderTests` 以註冊檔
為基準把每一個 moniker 都反推一次：

| 測試 | 抓的是 |
|---|---|
| 讀取端問過註冊檔宣告的每一個設定 | 忘了加進 `Read()`、moniker 打錯字、註冊檔有而程式沒讀 |
| 每一個設定都會改變快照 | 讀了值卻忘了指派給屬性 |
| 註冊檔的預設值等於程式的預設值 | 兩邊預設值分歧、列舉字面值改名 |
| 訂閱清單涵蓋每一個設定 | 反射篩選失效 |

沒有任何一份手抄的 moniker 清單，所以**不會出現「測試通過但少驗了新設定」**。

新的列舉值也要加進 `SqlAssistRegistrationTests.列舉的字面值不變` 的 `InlineData`——
那一份是刻意手寫的相容性鎖，不是重複。

## 條件式只能參照一個同分類的設定

`enableWhen` 與 `visibleWhen` 只能參照**同一個分類裡**的設定。跨分類參照不會有任何錯誤訊息：
殼層安靜地把整個設定丟掉，該分類的設定全被丟掉之後就成了空分類，而空分類預設不顯示
（`canBeVisibleWhenEmpty` 預設 `false`），於是整頁在設定視窗裡人間蒸發。

第一次實作時就踩了這個坑：11 項設定的 `enableWhen` 都寫了
`${config:sqlAssist.general.enabled}`，而它們不在 `general` 分類裡，結果「建議清單」與
「物件結構」兩頁完全不見。從程式碼、schema 驗證到建置都看不出任何異狀——
`SqlAssistRegistrationTests.條件式只參照同分類的設定` 就是為了讓它變成建置失敗。

代價是總開關無法讓其他頁變灰。這是 Unified Settings 的限制，不是設計選擇；
SSMS 自己的 `RadLangSvc.registration.json` 也只做同分類參照。

**同分類還不夠，設定的 `enableWhen` 只能參照一個設定。**
`sqlAssist.general.wildcardLayout` 原本寫成
`${config:sqlAssist.general.enabled} == 'true' && ${config:sqlAssist.general.expandWildcardOnTab} == 'true'`——
兩個參照都在 `general` 分類裡，殼層照樣安靜地把整項丟掉：設定頁上完全看不到
「SELECT * 展開後的欄位排版」，讀取時只拿得到 `NotPersisted`。改成單一參照就回來了，
`SqlAssistRegistrationTests.設定的條件式只參照一個設定` 擋下再犯。

這條限制只管**設定自己的** `enableWhen`；分類上 `messages`／`commands` 的條件式不受限，
SSMS 的 `SqlStudio.registration.json` 就有複合條件的 `visibleWhen`。

附帶的好處是設定頁的縮排跟著那個參照走：`wildcardLayout` 現在縮排在
「按 Tab 把 SELECT * 展開成欄位清單」底下，而不是和它並排在「啟用 SqlAssist」下面。
**選哪一個設定當參照，就是在選它排在誰底下。**

## 設定變多之後要怎麼分類

「建議清單」一頁已經有 10 項，混了觸發時機、內容來源、插入格式與語句展開四件事，
遲早得拆。拆法有兩條限制先決定了能拆到什麼程度，不先知道就會做白工。

**moniker 是使用者資料，不是分類名稱。** 設定值以 moniker 為鍵存放並跟著漫遊同步，
改名等於讓所有自訂過的使用者看起來全部回到預設。所以「顯示在哪一頁」與
「存在哪個鍵」必須分開處理：既有設定沿用歷史 moniker，只有新設定才從一開始就放對前綴。

**`placements` 與 `enableWhen` 只能二選一。** schema 允許用 `placements` 指定顯示分類
而不動 moniker，但同一項設定的 `enableWhen` 只要出現 `${config:...}` 就不能再寫
`placements`（`registration.schema.json` 的欄位說明明講）。20 項設定裡有 15 項帶條件式，
真要照「使用者想完成的工作」重分頁，需要搬動的十幾項裡有 11 項得整條拿掉——
代價不只是總開關關掉時不變灰，連縮排一起沒了：`wildcardLayout` 會從
「按 Tab 把 SELECT * 展開成欄位清單」底下掉出來變成並排的獨立項目，
而它在 Tab 展開關掉時毫無意義。

所以先做的是不必動 moniker 也不必動條件式的那一半，已經做完：`order` 改成依
「同一頁裡的哪一段」留號段（段隔 100、段內隔 10；建議清單是 100 觸發、200 內容來源、
300 插入格式、400 語句展開），每項設定與每個分類都補上 `additionalKeywords`——
這一欄先前一個都沒寫，搜尋「自動完成」「IntelliSense」「schema」「星號」「log」
全都是空的，而那才是使用者找不到設定最常見的原因，不是分類不對。

還沒做也暫時不做的是分頁本身。真要動樹狀結構，先用實驗版在實機比較
「少了 11 個縮排」和「一頁 10 項」哪個比較難用，別在驗證升級與漫遊資料前先改 moniker。

## 四件刻意<b>不</b>是設定的東西

- **清單引擎**：固定使用平台原生管線，舊的自製 WPF 清單已移除。
- **展開萬用字元的行寬**：固定 120 個字元。排法本身是設定，這個分界點不是——
  使用者感覺得到的是「一行還是好幾行」，落在 118 還是 124 他不會有意見。
- **清單最多顯示筆數**：模糊比對後的截斷上限固定 300。這是效能保險而不是偏好——
  使用者感覺不到差別，因為清單本來就要捲動，再多打一個字排名也整個重算。
- **預覽視窗的寬高**：拖曳握把記下來的是視窗狀態不是偏好，改存 VS 的
  `WritableSettingsStore`（`SqlAssist\Preview`）。放進 Unified Settings 等於
  每放開一次滑鼠就提交一次設定變更並廣播通知。上下與側邊各自記寬高；上下的
  寬度另有「自動延伸」狀態，雙擊任一握把可恢復目前擺放方式的預設尺寸。

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
顯示游標處物件的結構         預覽關成 off 時的唯一入口
重新整理建議                 改完資料表結構後用
程式碼片段…                  編輯內建與自訂片段
─────────────
設定…                        開啟 Unified Settings 並定位到 sqlAssist
關於與診斷…                  看版本、重要設定、健康檢查；可複製匿名診斷摘要
```

「關於與診斷」分成**概覽／設定摘要／診斷**三頁。複製出的摘要適合直接貼到公開 Issue，
不包含 SQL 文字、伺服器名稱、資料庫名稱或 Windows 使用者名稱；完整診斷紀錄仍可能包含
資料庫物件名稱，附檔前應先檢查內容。
