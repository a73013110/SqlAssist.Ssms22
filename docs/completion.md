# 建議清單

本頁只處理排名、列尾標記、清單引擎與 SSMS 內建 IntelliSense；候選來源、上下文與提交規則由
[文件路由](index.md)直接指向各自的葉文件。

## 清單內容與排名

在查詢編輯器輸入第一個字元後立即顯示建議，內容包含 T-SQL 關鍵字、內建函式、
程式碼片段，以及目前連線資料庫的 Table、View、Procedure、Function、Synonym
與 Schema。資料型別、全域變數（`@@ROWCOUNT`…）與指令碼自己宣告的變數不混在這一份
裡——它們各自只出現在文法只接受它們的那個位置，見下。

排名採用 fzf v2 的 Smith-Waterman 變體，並針對 SQL 識別字調整字元分類：
底線、井號、小老鼠與點號都視為分隔符，分隔符與 camelCase 轉折後方的字元
取得詞首加成。因此輸入 `libr` 時 `Lib_Reader` 就會排在第一，不必打到 `lib_re`。
命中的字元會在清單中以粗體標示。

排名分四層，高層打平才輪到低層：比對品質 ＞ 類別 ＞ 最近用過 ＞ 名稱長度。
「最近用過」刻意壓在類別之下——它曾經在上面，而編輯的實際順序保證那條規則會反咬：
使用者才剛從清單挑完 `FROM dbo.Loan l, dbo.Copy c`，游標一移到 `SET |` 或 `WHERE |`，
那幾張表就帶著加成蓋住他真正要挑的欄位。類別說的是「這個位置文法上要什麼」，
使用紀錄只是跨敘述的猜測。降到類別之下後它仍做原本有價值的那件事：同類別內把常用的
拉到前面。

49 筆 Snippet 不再以固定最高類別加成塞滿清單：沒有前綴時排在欄位、關鍵字與常用物件
之後，危險片段直接隱藏；輸入從捷徑開頭命中時才恢復最高加成，純子序列命中則維持低分。
每筆另帶 `SqlKeywordPosition`，語句級 DDL 不會混進 SELECT 欄位位置。這三層規則都在
Core，避免原生清單與測試用排名走出不同結果。

```text
s     → 顯示 SELECT、SET、ssf 等關鍵字與 Snippet，以及符合的資料庫物件
libr  → Lib_Reader 排第一
Tab   → 提交選取項
```

使用 `↑`、`↓` 選擇，`Tab` 或 `Enter` 提交，`Esc` 關閉，也可以直接用滑鼠點選。

## 列尾標記

名稱右側只標例外，大部分列一顆都沒有：人人有份的標記等於沒有，種類與結構描述已由
圖示與說明欄回答。判斷在 `Core/Completion/SuggestionMarks`，清單建立時定一次；圖示在
`SqlIcons`。平台不替列尾圖示顯示工具提示，意思寫在說明面板最上方，一個標記一行；
浮動預覽接手說明時不另外顯示。

| 標記 | 何時掛 |
|---|---|
| 危險 | `IsDestructive` 的片段：沒有前綴時隱藏，打出捷徑後要看得出它危險 |
| 已淘汰 | 型別建議 `TEXT`、`NTEXT`、`IMAGE`；資料行不因型別標，那一列選的是資料行 |
| 系統物件 | 系統物件與使用者物件並列時（`EXEC` 之後）；`sys.` 之後整份都是，不標 |
| 最近用過 | 與排名同一份 `SqlSuggestionUsage`，解釋它為什麼排在同類前面 |

## 清單引擎

清單由**平台原生的非同步 IntelliSense** 呈現：定位、螢幕邊界、捲動、滑鼠操作
與佈景主題都由編輯器負責，與其他擴充套件共用同一個 session。

排名不交給平台：平台預設的比對器沒有詞首感知，接上去 `libr` 又會排不到
`Lib_Reader`。因此本擴充匯出自己的 `IAsyncCompletionItemManager`，
沿用同一套模糊比對分數，並把命中區段交給平台畫粗體。

## 分類篩選

篩選列只在候選有兩類以上時出現，依固定順序只列這份清單有的分類。種類對應與取捨
（函式分三類、CTE 與暫存資料表歸資料表、結構描述與資料庫自成一類、只出現在專屬位置的
變數與型別等不分類、不設「其他」）在 `Core/Completion/SuggestionCategory`。沒按任何一顆
就是全部，多選取聯集；不改變 SQL 語境、排名或插入文字。

快捷鍵照畫面位置、與鍵盤數字列同序：由左至右 `Alt+1`～`Alt+9`，第十顆 `Alt+0`；
超過十類的沒有按鈕，只在全部裡。不用字母存取鍵：中文介面上「資料表＝T」沒有對應，只能硬背。

規則只有一條——**沒有命中的分類不能處於按下狀態**（`SuggestionCategoryFilter`）：
打字後沒命中的鈕變灰並跳起來，按下的全部跳起來就回到全部，灰色的按不下去，跳起來的
不會在命中回來時自己按回去。清單因此不會被篩空；平台對空清單的處理是顯示「無建議」或
退回上一份舊清單，兩種都是錯的畫面。完全沒有命中時照常關閉清單。

按鈕整組由來源以 `CompletionContext(items, filters)` 交出，**不**掛在項目上讓平台推：推出來
的是項目的串接順序，數字就對不上。每份清單建一組、不可變、不記上一輪。

## 與 SSMS 內建 IntelliSense 並存

SSMS 內建的 T-SQL IntelliSense 是舊版語言服務（MPF 的
`Microsoft.VisualStudio.Package.LanguageService`），由它自己的命令篩選器觸發，
不會因為有新版建議來源就讓位。兩份清單同時活著時，舊版會對著已經被換掉的狀態
算範圍，於是每退一格就跳一次「值未落在預期的範圍內。」或「並未將物件參考設定為
物件的執行個體。」。

**不要整個關掉它。** 它的總開關 `languages.sql.intelliSense.enableIntellisense`
底下用 `enableWhen` 掛著 `underlineErrors`（紅色錯誤波浪線）與 `autoOutlining`
——關掉總開關等於連錯誤檢查一起關掉，而錯誤檢查是這個擴充完全沒有提供的東西。
換到的是清單不打架，付出的是整份語法檢查，划不來。

要擋的只有「打字時自動彈出的那份清單」，而那是另一個旗標。預設開啟的
**「只使用 SqlAssist 的建議清單」**（`sqlAssist.suggestions.suppressNativeMemberList`）
把舊版語言服務的 `LANGPREFERENCES2.fAutoListMembers` 設成 0，其餘一切照舊。
實作與逐條理由見 `Ssms22/Settings/NativeMemberList`。

那個旗標在 SSMS 的設定頁上看得到也改得動——語言 → Transact-SQL → 一般 →
陳述式完成 → 自動列出成員，所以寫它的不只這個擴充。使用者在那裡勾回來之後，
下一次套用（套件載入、建立 SQL 編輯器、設定變更）會再寫成 0；那是本項開著時的
預期行為。隔壁的「參數資訊」對應 `fAutoListParams`，這個擴充不動它。

分得開是因為決定「要不要把清單畫出來」的那一行讀的就是它。
`Source.HandleCompletionResponse` 只在下列條件成立時才呼叫 `completionSet.Init`：

```text
AutoListMembers || reason == CompleteWord || reason == DisplayMemberList
```

因此關掉之後：打字不再彈出，`Ctrl+Space` 與 `Ctrl+J` 仍然叫得出舊版清單
（這一段是刻意留著的缺口，兩邊都會回應），而波浪線走的是另一條路——
`Source.OnIdle` 到 `BeginParse(ParseReason.Check)`——完全不看這個旗標。

順帶收掉的是 `RadLangSvc.Source.OnCommand` 裡「Backspace／Delete 時重新篩選舊版
清單」那一段：它只在清單顯示中才執行，而那正是刪字元時跳錯誤對話框的來源。

三件事讓這條路可靠：`HandleCompletionResponse` 是 internal 且非虛擬，RadLangSvc
覆寫不了；它的 `LanguagePreferences` 子類別（`SqlIntelliSenseSettings`）也沒有覆寫
`AutoListMembers`；而 `LanguagePreferences` 實作 `IVsTextManagerEvents2`，所以寫下去
立刻生效，不必重開查詢視窗——`enableIntellisense` 是在 `RadLangSvc.Source` 的建構式
裡抓進欄位的，那個才需要重開。

早期版本改用執行期硬關對方 session 的做法，但在 SSMS 22 兩條管線共用同一條
命令鏈，整批關掉會連帶收掉自己剛觸發的那一個，反而讓清單完全不出現。
那條路徑已經移除。
