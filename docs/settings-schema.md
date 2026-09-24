# 設定結構與新增規則

本頁包含新增一個設定要動哪幾處、條件式的限制，以及分類怎麼切。
硬規則見[設定護欄](rules-settings.md)，既有項目清單見[設定入口](settings-entries.md)。

## 新增一個設定

要動四處，順序無所謂，但**四處都要**：

| # | 檔案 | 加什麼 |
|---|---|---|
| 1 | `src/SqlAssist.Ssms22/SqlAssist.registration.json` | 型別、`default`、`title`、`description`、`order`；數值加 `minimum`／`maximum`，列舉加 `enum` 與 `enumItemLabels` |
| 2 | `Core/Settings/SqlAssistSettings.cs` | 強型別屬性，預設值必須等於註冊檔的 `default` |
| 3 | `Core/Settings/SqlAssistMonikers.cs` | 一個 `const string`。訂閱清單 `All` 由反射產生，不必手動加 |
| 4 | `Core/Settings/SqlAssistSettingsReader.cs` | `Read()` 裡的一行對應；列舉要加解析、數值要套 `SqlAssistLimits` 的收斂 |

漏掉 2、3、4 任何一處都是**建置失敗**：`SqlAssistSettingsReaderTests` 以註冊檔為基準把每一個
moniker 反推一次，沒有任何一份手抄清單，所以不會出現「測試通過但少驗了新設定」。

新的列舉值也要加進 `SqlAssistRegistrationTests.列舉的字面值不變` 的 `InlineData`——
那一份是刻意手寫的相容性鎖，不是重複。

新設定的分類要**先決定再寫**：`enableWhen` 只能參照同分類，所以一項設定要縮排在
誰底下，就決定了它只能待在哪一頁。詳見下面兩節。

## 條件式只能參照一個同分類的設定

`enableWhen` 與 `visibleWhen` 只能參照**同一個分類裡**的設定。跨分類參照不會有任何錯誤訊息：
殼層安靜地把整個設定丟掉，該分類的設定全被丟掉之後就成了空分類，而空分類預設不顯示
（`canBeVisibleWhenEmpty` 預設 `false`），於是整頁在設定視窗裡人間蒸發。

從程式碼、schema 驗證到建置都看不出異狀，`SqlAssistRegistrationTests` 把它變成建置失敗。

代價是總開關無法讓其他頁變灰。這是 Unified Settings 的限制，不是設計選擇；
SSMS 自己的 `RadLangSvc.registration.json` 也只做同分類參照。

**同分類還不夠，設定的 `enableWhen` 只能參照一個設定。**兩個同分類參照用 `&&` 串起來，
殼層照樣安靜地把整項丟掉，讀取時只拿得到 `NotPersisted`；同一組測試擋下再犯。

這條限制只管**設定自己的** `enableWhen`；分類上 `messages`／`commands` 的條件式不受限，
SSMS 的 `SqlStudio.registration.json` 就有複合條件的 `visibleWhen`。

附帶的好處是設定頁的縮排跟著那個參照走：`wildcardLayout` 縮排在
「按 Tab 把 SELECT * 展開成欄位清單」底下，「EXEC 展開時包含選擇性參數」縮排在
「在 EXEC 之後展開完整的參數清單」底下。**選哪一個設定當參照，就是在選它排在誰底下**，
而那個被參照的設定必須跟它同一頁——所以縮排關係實際上綁死了分頁。

## 分類依「他想做什麼」切，不依「誰觸發的」

依用途分頁：**一般**（總開關、輸入時的改寫與更新檢查）、**建議清單**（清單何時彈出、列些什麼）、
**插入與展開**（最後寫進編輯器的是什麼文字）、**物件結構**、**區塊配對**、**診斷**。

照觸發路徑分類的話，同一件事會散在兩頁（Tab 觸發的 `SELECT *` 與提交觸發的語句展開）。
使用者要找的是「插進去的東西長什麼樣」，不是「這是哪一條程式碼路徑」。

**moniker 是使用者資料。** 設定值以 moniker 為鍵存放並跟著漫遊同步，改前綴等於讓
所有自訂過的人回到預設。`migration` 規則只吃 `VsUserSettingsRegistry`／`SettingsManager`／
`AppidProperties` 三種舊存放區，**沒有 unified→unified 的改名規則**，自己寫搬遷則要把舊 moniker
永久藏在註冊檔裡。改名的成本只會隨使用者數往上。

**`placements` 與 `enableWhen` 只能二選一。** schema 允許用 `placements` 指定顯示分類
而不動 moniker，但同一項設定的 `enableWhen` 只要出現 `${config:...}` 就不能再寫
`placements`（`registration.schema.json` 的欄位說明明講）。縮排就是 `enableWhen`
生出來的，用了 `placements` 就會讓子項從父項底下掉出來變成並排的獨立項目。SSMS 22 自帶的
註冊檔也沒有一份用它，所以分頁一律靠 moniker 本身。

`order` 依「同一頁裡的哪一段」留號段（段隔 100、段內隔 10）：建議清單是
100 觸發、200 內容來源；插入與展開是 100 名稱格式、200 欄位展開、300 語句展開。
用連號的話每插一項就要把整頁重排一次，而 `order` 改動在設定頁上看不出來。
