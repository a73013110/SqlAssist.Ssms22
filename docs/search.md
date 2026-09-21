# SQL Search

跨來源找「這個名字或這段文字在哪裡」的工具窗。它是一個框架而不是單一功能：Core 定契約與
聚合，Metadata 放來源，Ssms22 只接線。

入口有兩個：**SqlAssist 工具列**的第三顆按鈕，以及**工具 → SqlAssist → SQL Search**。
工具列那一顆的字是 **Search** 而不是 SQL Search：那一列是 `History｜Favorites｜Search`，
三顆都帶圖示與字，多出來的「SQL」在三顆都屬於 SqlAssist 的工具列上說不出新資訊，卻實際
佔掉工具列寬度；完整名稱留在 Tooltip 與選單上那一顆。
工具列那一顆走 `CommandPlacement` 而不是第二顆按鈕，外觀與選單上那一顆完全相同；
順序排在 History／Favorites 後面，理由與選單分成兩個群組相同——前兩顆找的是自己寫過的
SQL，這一顆找的是伺服器上的物件。沒有鍵繫結，理由見 `CommandIds.ShowSqlSearch`。

## provider 契約

一個來源實作 `Core/Search/ISearchProvider`：宣告自己的分類、把命中推進 `ISearchSink`，
其餘交給 `SearchAggregator`。走 sink 而不是回傳清單是這個框架唯一的效能接縫——名稱命中
在使用者還在打字時就要上畫面，定義本文慢慢補；收集完才回傳的話，整輪延遲等於最慢那一個來源。

### 加一個新來源

1. 實作 `ISearchProvider`；`Id` 跨版本不得更名。
2. 自己一份分類表（照 `SqlAgentJobSearchCategories`）。分類 `Id` 會寫進使用者偏好，同樣不得更名。
3. 自己一型導航酬載，掛在 `SearchHit.ActivatePayload` 上；Core 不解讀它。
4. 在 `Ssms22/Search/SqlSearchProviders` 加一個巢狀來源類別。
5. 在 `SqlSearchActivation` 加一個 `is` 分支。

Core、聚合器、種類下拉、清單樣板與預覽一個字都不必改。這就是 `SqlAgentJobSearchProvider`
加進來時付的全部代價，而它跨的是伺服器不是資料庫、資料在 `msdb`、權限不足還是常態。

provider 得自己守四條，每一條都是「少做一次就看不出來」：`SearchQuery.Targets` 與分類過濾
在**取資料之前**問（掃回來再丟等於使用者關掉的那一段一毫秒都沒省到，而且不要的候選還算進
預算）；`TryReport` 回 false 立刻停；`DbException` 一律降級不外擲。

## 兩條篩選軸

**物件種類**（`SearchCategory`）問「這是哪一種東西」，**比對位置**（`SearchMatchTarget`）問
「對上的是它的哪裡」。分開的理由是資料行：`CopyNo` 命中講的是「`Cat_BookCopy` 上有一行叫
這個名字」，而使用者勾「只看資料表」時要的正是它。把資料行做成一種物件種類，那一勾會讓它整組消失。

種類清單刻意不寫在 Core：`SqlObjectKind` 住在 Metadata，而相依方向是 Metadata → Core。
每個 provider 自己宣告，目錄那一份的對應表在 `SqlCatalogSearchCategories`。

## 排名、去重與名稱／本文

名稱與資料行走 `FuzzyMatcher`（詞首加成，一律不分大小寫——`PUBLISHER` 打成 `publisher`
仍要命中）；定義本文走 ordinal 子字串，分數是「提到幾次」。本文不走模糊比對：一份幾千行的
定義對任何三個字母都命中，分數還很高。

兩種尺度不互相比較。`SearchMatchTargets.GroupOrder` 先把名稱、資料行、本文分成三段，分數
只在段內比；照列舉值排的話本文會插在名稱與資料行中間。去重鍵不含比對位置：`Loan` 同時被
名稱與本文命中時那仍然是同一張表，兩列指向同一個地方。留下的是排名高的那一份，不是先到的。

## 掃描預算與部分結果

沿用 [SQL Memory 搜尋](sql-memory-search.md#掃描預算)那一套：上限落在讀取迴圈上不落在查詢
條件裡，用盡就回傳已命中的部分並標記 `IsPartial`，不回空的也不悄悄截斷。筆數上限拆成
「每個 provider」與「排名後的總數」兩個，才不會由誰先排到執行緒決定誰被砍。

`IsPartial` 是一個布林，而畫面上要說的話有三句，所以另外要問兩處：`Failures`（provider 擲了
例外）與 `Progress` 上的 `IsUnavailable`。「沒掃完」叫使用者縮小範圍，「讀不到」叫他去看權限
——混成一句的症狀是使用者照前一句改三次關鍵字，而那個資料庫一次都沒被搜到。

provider 擲例外那一條（`Failures`）寫到畫面上時用的是 `ISearchProvider.DisplayName`，不是 `Id`：
Id 跨版本不得更名，而它不在介面上任何地方出現過——「『catalog』這一輪失敗」對使用者來說指不到
自己勾的哪一個範圍。診斷仍然記 Id。

回報分兩件東西：`UnavailableReason` 是一句給人看的話，Core 不解讀；`SearchUnavailableKind`
只有 `Unknown` 與 `Denied`。不細分是因為呈現那一層要的答案只有一個——下一步是「重試或換
條件」還是「去要權限」，而連不上、逾時與離線的下一步一樣。

`Denied` 是斷言，三道關卡都「說得準才說」：provider 要伺服器給了權限錯誤碼；`BudgetedSink`
在同一個來源說了兩種時退回 `Unknown`（句子留第一句，留哪一句都說得通，而留第一個種類等於
斷言由賽跑決定）；`SqlSearchBrowserModel.Surface` 只在**每一個**讀不到的來源都是 `Denied`
時才回 `SqlSurfaceState.Denied`。其中之一就換抬頭的話，使用者去要了權限，那個連不上的來源
下一輪還是讀不到，而畫面上看不出他要錯了東西。代價不對稱：斷言不足只是少說一句話。

錯誤碼由 `SqlServerErrorCodes` 認（229／230／262／297／300／916／4060）。18456 **不在**
名單裡：登入失敗是認證不是授權，下一步是去看帳號密碼或 Entra 權杖。

它靠**反射**讀 `Number`。兩個理由缺一都還是要反射：Metadata 只依賴 `System.Data`，而
netstandard2.0 的 `DbException` 上沒有錯誤碼；執行期丟出來的又是
`Microsoft.Data.SqlClient.SqlException`，與 `System.Data.SqlClient` 那一份是兩個型別，參照了
也一次都不會成立，症狀是安靜地永遠回 `Unknown`。代價只在失敗的那一次付。

## 索引策略

`SqlCatalogSearchIndex` 與 `SqlMetadataCatalog` 是分開的兩份，刻意不重用。那四層是**按需**
載入的，搜尋要的正好相反：一次要全部。併在一起的話失效策略互相打架——搜尋一次就把整個資料庫
的定義本文灌進按鍵路徑上的常駐快取，而建議清單的快取被自己的資料擠掉。連線與快取鍵則
**必須**共用，自己拼一份鍵的症狀是同一個資料庫拿到兩份索引。

索引分兩段。物件、資料行與結構描述便宜且必備；定義本文沒有上界，是第一次搜尋最貴的一段，
不搜本文時連那條查詢都不送。之後改主意走 `TryAddDefinitions` 補第二段，第一段不重掃。

版本戳是 `MAX(modify_date)` 加物件數兩個維度：只看時間分不出「什麼都沒變」與「剛好卸除了
最後改過的那一個」。按「重新整理」走 `Invalidate` 而不是 `Clear`——使用者說的是「我知道它舊了」，
改一個預存程序再按一次，付的是一條物件查詢加那一個程序的本文。`Clear` 留給換連線。

記憶體上限以**位元組**計：單一索引 64 MiB 定義本文，整份快取 256 MiB。照份數算的症狀是四個
大庫把行程撐爆，而四個小庫又白丟明明留得住的東西。滿了淘汰最久沒用到的，但永遠留一份——
一份比預算還大的索引仍然要能用。超出上限只留名稱並標記不完整；安靜地少一半結果最糟，
使用者會以為那個字串不存在。只索引使用者明確指名的資料庫，預先索引全部是明文禁止的。

## 關掉重開記得什麼

只記「怎麼比對」那三項（比對位置、大小寫、全字），走 `SqlAssistState` 的狀態存放區，
不是 Unified Settings——它們是工具列上隨手切的狀態，進了設定頁等於每按一下就提交一次設定
變更並廣播通知，理由與[預覽視窗](preview-window.md)的尺寸相同。三項收成一個字串，
認不得就整組回預設，不半套還原。

伺服器、資料庫與物件種類**不記**，理由與那兩顆按鈕的其餘規則見[搜尋範圍](search-scope.md)。

## 不支援

- **連結伺服器**：沒有四段式名稱的索引。指名伺服器時整輪不回結果，而不是拿本機同名的物件
  充當對面那台的答案。那條路要的是 `SqlCatalogQualifier` 與 `OPENQUERY`，不是換個資料庫。
- **指令碼自己宣告的東西**（`#Loan`、資料表變數、CTE）：一列都不在 `sys.objects` 上。
- **資料列內容**：搜的是名稱與定義，不是資料。
- **regex 與 facet 語法**：`SearchOptions` 留了位元，目前只有大小寫與全字。
- **持久化索引**：索引只活在行程裡，關掉 SSMS 就沒了。
