# SQL Search

本頁定義跨來源搜尋的 provider、排序／合併、部分結果、索引與清單的批次複製；命中標示與啟用結果分別見
[命中高亮](search-highlight.md)及[結果導航](search-navigation.md)。

入口是 SqlAssist 工具列的 **Search**（位於 History／Favorites 後）與
**工具 → SqlAssist → SQL Search**；完整名稱留在 Tooltip 與選單，沒有鍵盤捷徑。

## provider 契約

來源實作 `Core/Search/ISearchProvider`，宣告分類並把命中推進 `ISearchSink`；
`SearchAggregator` 負責聚合。串流 sink 讓快速的名稱命中先顯示，不必等最慢來源完成。

### 加一個新來源

1. 實作 `ISearchProvider`；跨版本不得更名 `Id`。
2. 提供分類表；分類 `Id` 會寫入偏好，同樣不得更名。
3. 提供自己的導航酬載，掛在 `SearchHit.ActivatePayload`；Core 不解讀。伺服器上的東西實作
   `ISqlSearchTarget`，帶著 provider 建構時收到的 `SqlSearchOrigin`，見[結果導航](search-navigation.md#伺服器跟著那一筆走)。
4. 在 `Ssms22/Search/SqlSearchProviders` 加入來源類別。
5. 在 `SqlSearchActivation` 加入對應分支。辨識酬載型別**只准**發生在那裡，清單樣板、圖示與
   預覽只讀 `SearchHit` 的欄位；預覽要不要讀定義也問它（`DefinitionOf`）。

provider 必須在取資料前套用 `SearchQuery.Targets` 與分類；`TryReport` 回 false 立即停止；
`DbException` 降級為來源失敗，不外擲。種類下拉、清單與預覽不因新來源改動。

## 兩條篩選軸

`SearchCategory` 是物件種類，`SearchMatchTarget` 是命中位置。資料行命中仍歸屬資料表，
否則「只看資料表」會錯誤排除資料行命中的表。種類由各 provider 宣告；Core 不依賴
Metadata 的 `SqlObjectKind`，目錄來源對應在 `SqlCatalogSearchCategories`。

## 排名、合併與名稱／本文

名稱與資料行在未開修飾時用 `FuzzyMatcher`；大小寫或全字任一開啟後改走
`SearchIdentifierMatch` 的字面比對。定義本文一律用字面子字串，分數是出現次數；
模糊搜尋本文會讓長定義以零散字母取得不合理高分。字面命中仍向 `FuzzyMatcher` 取分，
避免同一批結果出現第二套排序。名稱、本文與高亮換算共用 `SearchQuery.Matcher`，見[唯一實作](shared-components.md)。

`SearchMatchTargets.GroupOrder` 先依名稱、資料行、本文分組，再於組內比分數；不同尺度不互比。
同一物件只有一列，去重鍵不含命中位置或資料行名稱。代表取排名最高者，其餘攤平放入
`SearchHit.Merged`；呈現讀 `SearchHit.Matches`，才能保留所有命中原因。

資料行命中的標題與去重鍵都必須指向所屬物件，不接資料行名稱。
含資料行的鍵只供 `SearchExamineCounter` 表示掃描位置。

## 掃描預算與部分結果

沿用 [SQL Memory 搜尋](sql-memory-search.md#掃描預算)：限制放在讀取迴圈，耗盡時回傳既有命中並標記
`IsPartial`。每個 provider 與排名後總數各有限額，避免由執行先後決定保留來源。

畫面需分清三種狀態：`IsPartial` 是沒掃完；`Failures` 是 provider 失敗；`Progress.IsUnavailable`
是來源讀不到。失敗訊息使用 `ISearchProvider.DisplayName`，診斷才用穩定但不面向使用者的 `Id`。

`UnavailableReason` 是顯示文字；`SearchUnavailableKind` 只分 `Unknown` 與 `Denied`。
只有 provider 收到明確權限錯誤、同一來源未混入其他種類，且所有不可用來源都是 `Denied` 時，
`SqlSearchBrowserModel.Surface` 才顯示權限不足；其餘都用可重試的未知失敗。

`SqlServerErrorCodes` 將 229、230、262、297、300、916、4060 視為權限錯誤；18456 是認證失敗。
錯誤碼以反射讀取，因 Metadata 只依賴 `System.Data`、netstandard2.0 的 `DbException` 沒有 Number，
執行期例外則來自 `Microsoft.Data.SqlClient`。

## 索引策略

`SqlCatalogSearchIndex` 不與按需載入的 `SqlMetadataCatalog` 共用資料，但必須共用連線與快取鍵。
搜尋一次需要整批資料，若灌入補全的常駐快取，兩邊的載入與淘汰策略會互相干擾。

索引分兩段：物件、資料行、結構描述先載入；只有搜尋本文才以 `TryAddDefinitions` 補定義，
不重掃第一段。版本戳同時使用 `MAX(modify_date)` 與物件數，才能辨識刪除最後修改物件。
重新整理呼叫 `Invalidate`，保留可增量更新的資料；換連線才 `Clear`。

單一索引最多 64 MiB 定義本文，整體快取 256 MiB，依最久未使用淘汰但至少保留一份。
單份超限時只留名稱並標記不完整，不得靜默漏結果。只索引使用者明確指定的資料庫，
禁止預先索引所有資料庫。

## 多選與批次複製

多選互動與 SQL Memory 同一份，見[清單列](ui-rows.md#多選)。一輪的答案整份在手上（分批套上只為了
分攤版面），所以沒有背景讀取：全選時還沒套上的那幾批先補完再複製。勾選以 `DedupeKey` 為鍵，
重新整理與重排照鍵保留。

欄位是名稱（限定名稱，沒有路徑概念的來源用標題）、種類、伺服器、資料庫、命中部位與命中資料行
（`SqlSearchRow.CopyColumns`）；伺服器與資料庫取 provider 的脈絡膠囊，沒有就留空，不含定義本文。
平常的 Ctrl+C 仍是複製焦點列的限定名稱。

## 關掉重開記得什麼

只將比對位置、大小寫、全字存進 `SqlAssistState`；後兩項的格式是 `TextMatchState`，
與 SQL Memory 共用，未知格式整組回預設。
伺服器、資料庫與物件種類不保存，見[搜尋範圍](search-scope.md)。

## 不支援

- 連結伺服器與四段式名稱。
- 指令碼內宣告的暫存表、資料表變數與 CTE。
- 資料列內容；只搜尋名稱、結構與定義。
- regex、facet 語法與持久化索引。
