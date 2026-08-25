# SqlAssist for SSMS 22

針對 SQL Server Management Studio 22.9.x 開發的 T-SQL 生產力擴充套件。
目前版本為 **0.10.0**。

## 專案結構

```text
src/SqlAssist.Core       netstandard2.0，無 Visual Studio 相依，可完整單元測試
  Matching/              詞首感知的模糊比對與命中區段
  Parsing/               T-SQL 詞法器與語句範圍模型（別名解析）
  （其餘）               語彙狀態、識別字解析、上下文分析、Snippet、設定

src/SqlAssist.Metadata   netstandard2.0，只依賴 System.Data
                         四層按需載入的資料庫中繼資料與快取

src/SqlAssist.Ssms22     net48 VSIX
  Completion/            平台原生非同步 IntelliSense 的來源、排名器與提交管理員
  QuickInfo/             滑鼠停留的物件結構提示
  Preview/               浮動結構預覽視窗
  Options/               工具→選項 的設定頁
```

核心邏輯刻意集中在沒有 Visual Studio 相依的兩個專案，因此排名、解析與
中繼資料對應都可以在不啟動 SSMS 的情況下驗證。目前共 357 項單元測試。

## 功能

### 建議清單

在查詢編輯器輸入第一個字元後立即顯示建議，內容包含 T-SQL 關鍵字、Snippet，
以及目前連線資料庫的 Table、View、Procedure、Function、Synonym 與 Schema。

排名採用 fzf v2 的 Smith-Waterman 變體，並針對 SQL 識別字調整字元分類：
底線、井號、小老鼠與點號都視為分隔符，分隔符與 camelCase 轉折後方的字元
取得詞首加成。因此輸入 `libr` 時 `Lib_Reader` 就會排在第一，不必打到 `lib_re`。
命中的字元會在清單中以粗體標示。

```text
s     → 顯示 SELECT、SET、ssf 等關鍵字與 Snippet，以及符合的資料庫物件
libr  → Lib_Reader 排第一
Tab   → 提交選取項
```

使用 `↑`、`↓` 選擇，`Tab` 或 `Enter` 提交，`Esc` 關閉，也可以直接用滑鼠點選。

### 清單引擎

預設使用**平台原生的非同步 IntelliSense**：清單的定位、螢幕邊界、捲動、滑鼠操作
與佈景主題都由編輯器負責，與其他擴充套件共用同一個 session。

排名不交給平台：平台預設的比對器沒有詞首感知，接上去 `libr` 又會排不到
`Lib_Reader`。因此本擴充匯出自己的 `IAsyncCompletionItemManager`，
沿用同一套模糊比對分數，並把命中區段交給平台畫粗體。

`工具 → 選項 → SqlAssist → 建議清單 → 清單引擎` 可以切回自製 WPF 清單
（`custom`）。那是後備選項，已知限制是只能用鍵盤操作，而且會與 SSMS 內建清單
同時出現。

SSMS 內建的 T-SQL IntelliSense 由它自己的命令篩選器觸發，不會因為有新版建議
來源就讓位。預設會在本擴充的清單被觸發的那一刻把它關掉一次——不是每一次按鍵
都關：那會在舊版語言服務還在計算時把 session 抽掉，反而會跳出
「值未落在預期的範圍內。」。兩份清單同時活著時，退格也會踩到同一個問題。

**想徹底避免兩份清單互搶，建議直接在「工具 → 選項 → 文字編輯器 →
Transact-SQL → IntelliSense」關閉 SSMS 內建的 IntelliSense**，
再把「關閉 SSMS 內建 IntelliSense 清單」也關掉。

### 內建 Snippet

| 輸入 | 展開結果 | 接續行為 |
|---|---|---|
| `ssf` | `SELECT * FROM ` | 接著只顯示 Table／View |
| `ap` | `ALTER PROCEDURE ` | 接著只顯示 Procedure |
| `af` | `ALTER FUNCTION ` | 接著只顯示 Function |
| `select` | `SELECT` | — |
| `from` | `FROM` | — |

快捷詞不會在 SQL 字串、註解、雙引號識別字或方括號識別字內展開。

### 關鍵字自動大寫

打完 `select` 再按空白鍵就得到 `SELECT`，不必先按 Tab 提交建議。
`inner`、`join`、`on`、`desc` 等關鍵字同樣適用；觸發時機是任何無法構成識別字的
字元——空白、逗號、括號、分號與運算子。

刻意不做成「用空白鍵提交清單選取項」：清單當下選中的可能是別的東西，
那種做法會把使用者根本沒要的名稱寫進編輯器。這裡只改寫剛打完的那一個字，
與清單開不開著無關，結果完全可預測。

下列情形不動：已經是大寫、限定字後方的名稱（`dbo.select`）、變數（`@select`）、
字串與註解內、方括號與雙引號識別字內（`[select]` 是欄位名稱）。

由 `工具 → SqlAssist → 關鍵字轉大寫` 開關控制；關掉它不影響清單裡的關鍵字建議。

### 依上下文縮小建議範圍

| 游標前方 | 只顯示 | 提交行為 |
|---|---|---|
| `FROM`、`JOIN`、`UPDATE`、`INTO` | Table、View | 插入名稱 |
| `ALTER PROCEDURE` | Procedure | 展開完整 ALTER 定義 |
| `ALTER FUNCTION` | Function | 展開完整 ALTER 定義 |
| `EXEC`、`EXECUTE` | Procedure | 插入名稱 |
| `dbo.`、`[dbo].` | 該結構描述的物件 | 插入名稱 |
| `u.`（`u` 是敘述中的別名） | 該資料表的欄位 | 插入欄位名稱 |

`ap` → `Tab` → 選取程序 → `Tab`，編輯器會直接放進該程序可執行的完整定義，
可以立刻修改並更新。定義開頭的 `CREATE` 或 `CREATE OR ALTER` 會改寫成 `ALTER`，
主體完全不動（主體裡的 `CREATE TABLE #tmp` 之類的語句不受影響）。

### 欄位建議

輸入 `別名.` 或 `資料表名稱.` 時列出該資料來源的欄位，並顯示型別、NULL 與 PK。

別名解析需要看得到游標**後方**的文字：

```sql
SELECT u.| FROM dbo.Lib_Reader u
```

FROM 子句在游標之後，只看前文永遠解析不出 `u`——而編輯既有查詢正是最常
遇到這種情形的時候。因此上下文分析改用完整文字加游標位置的多載。

範圍以括號深度界定，子查詢內只看得到子查詢自己的 FROM 子句。
衍生資料表與資料表變數查不到欄位中繼資料，此時維持原本的物件清單。

緊接在 `FROM`、`JOIN`、`EXEC` 之後的限定字一律當結構描述：
`FROM dbo.` 要列出 dbo 的物件，而 `FROM u.` 這種寫法並不存在。

沒有限定字的位置（`SELECT |`、`WHERE |`、`ON |`）也會列出敘述看得到的欄位，
而且排在資料庫物件之前——在這些位置要的幾乎都是欄位。敘述裡有兩個以上的
資料來源時，插入的文字會自動補上別名，否則 `SELECT Name FROM A a JOIN B b`
會因為欄位名稱模稜兩可而執行失敗。

這條路徑只使用**已經在快取裡**的欄位，不會為了列清單去等一次查詢；沒命中就
這一輪不顯示，背景預先載入補上之後下一次按鍵就有了。

### 物件結構提示

滑鼠停留在資料表、檢視、預存程序或函式的名稱上，顯示物件種類、完整名稱、
欄位總數與前 8 個欄位的型別、NULL、PK、IDENTITY、COMPUTED。
支援方括號、雙引號、結構描述限定與暫存表名稱。

停留在別名上會顯示它所指的資料表；停留在 `u.Name` 的欄位上則顯示該欄位的
型別與屬性。限定詞確實是資料來源但查無該欄位時不顯示提示，
不會退回去猜同名的資料庫物件。

提示刻意只給一眼看得完的份量。提示視窗不能捲動也不能選取，放再多也讀不完，
所以最後一行是可點擊的**「開啟完整結構」**，看不完的那一半交給浮動結構預覽。

這條路徑在滑鼠移動的軌跡上，因此**只讀快取**：中繼資料沒命中就只顯示標題，
背景補上之後下一次停留就有內容；連線也只用已經解析好的目錄，絕不在這裡向
SSMS 詢問目前連線——那個呼叫有 UI 執行緒相依性，忙的時候會直接變成打字延遲。

### 浮動結構預覽

三條入口共用同一個視窗：建議清單開著時按**向右鍵**、滑鼠停留提示裡的
「開啟完整結構」、以及「工具 → SqlAssist → 顯示游標處物件的結構」。

| 分頁 | 內容 |
|---|---|
| 欄位 | 序號、名稱、型別、NULL、PK、IDENTITY、計算欄位運算式、預設值 |
| 索引 | 名稱、種類、索引鍵欄位與排序、INCLUDE 欄位、篩選條件 |
| 外來鍵 | 名稱、欄位對應、ON DELETE／ON UPDATE 動作 |
| 參數 | 模組類物件的參數與 OUTPUT 標示 |
| 指令碼 | 可直接執行的完整 CREATE 指令碼，有 T-SQL 語法著色 |

空的分頁不會出現。底部直接寫出摘要，例如
`23 個欄位　PK：Id ASC　3 個索引　1 個外來鍵`。

#### 為什麼不是工具視窗

視窗掛在編輯器自己的**空間保留管理員**上，也就是 IntelliSense 清單與提示視窗
用的那一套機制，並且排在內建的 `completion` 之後。這帶來三件單靠 WPF `Popup`
做不到的事：

- **位置由平台計算**：自動貼在建議清單旁邊、避開它已經佔住的空間，撞到螢幕
  邊界就翻到另一側，不必自己量清單有多寬。
- **點進去不會關掉清單**：焦點落在這種視窗裡時，編輯器仍然算「持有焦點」，
  所以可以用滑鼠把 `CREATE TABLE` 整段拉選起來，建議清單不會消失。
- **跟著編輯器走**：捲動、切換視窗、關閉查詢視窗時一起處理掉。

顯示時**不會主動搶焦點**，游標留在編輯器裡，可以繼續打字；滑鼠滾輪不需要焦點
就能捲動，要拉選文字時點一下才把焦點交出去。

#### 操作

| 按鍵／動作 | 行為 |
|---|---|
| `→` | 建議清單開著時展開預覽；沒有清單時照常右移游標 |
| `↑` `↓` | 展開狀態下跟著選取換內容，維持展開 |
| `←` | 展開狀態下收合；沒展開時照常左移游標 |
| `Esc` | 收掉預覽（清單開著時就照常先關清單，預覽跟著收） |
| `Tab` `Enter` | 挑選完成，預覽跟著關 |
| 滑鼠拉選 ＋ `Ctrl+C` | 複製選取的指令碼 |
| 右鍵 | 複製選取內容／複製完整指令碼 |
| 右下角握把 | 拖曳調整大小，尺寸會寫回設定檔 |

指令碼分頁裡放的是一個**真正的唯讀編輯器**，語法著色、拉選、捲動與尋找都由
編輯器自己處理。它不在 SSMS 的命令繞送鏈上，所以鍵盤打不進去（天然唯讀），
`Ctrl+C` 與右鍵選單則由擴充自己接上。資料格分頁以儲存格為選取單位，
可以拉選再 `Ctrl+C`（含標題）。

指令碼是可以直接執行的：主索引鍵寫進 CREATE TABLE 的條件約束，
唯一條件約束寫成 ALTER TABLE，計算欄位寫成 `AS 運算式`。

#### 不卡頓的做法

- **視窗預先建好**：建議清單第一次開啟之後，以 `ApplicationIdle` 優先權在背景
  建立視窗與內嵌編輯器——那是兩次按鍵之間 UI 執行緒真的沒事做的時候。
- **沒展開就不做事**：方向鍵掃過二十項時，預覽只記下選到誰，不畫也不查。
- **由便宜到昂貴**：第四層快取命中就直接畫完；只有第二層命中就先畫欄位、
  索引與外來鍵稍後補上；兩層都沒有才先畫標題並啟動節流計時器。
- **節流**：換選取後預設等 220 毫秒才真的查資料庫，按著方向鍵一路往下時
  中途的每一項都不會送出查詢。
- **只填看得見的分頁**：五個分頁一起填等於每換一個物件就多四次版面計算，
  切過去時再填。

## 設定

**工具 → 選項 → SqlAssist**，分為「一般」「建議清單」「結構預覽」三頁，存檔後立即生效。
設定同時寫入下列檔案，兩邊看到的是同一份狀態：

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\settings.json
```

**工具 → SqlAssist** 提供即時開關與動作：

```text
啟用 SqlAssist / 即時建議
Tab 快捷展開 / 關鍵字轉大寫 / Procedure／Function 選擇器 / 結果格命令
顯示游標處物件的結構 / 顯示診斷狀態 / 重新整理建議 / 設定… / 編輯 settings.json
非同步建議追蹤
```

快捷鍵：`Ctrl+Alt+Shift+S` 全域開關

設定檔範例：

```json
{
  "enabled": true,
  "features": {
    "tabExpansion": true,
    "keywordUppercase": true,
    "objectPicker": true,
    "objectHover": true,
    "resultGridCommands": true
  },
  "suggestions": {
    "enabled": true,
    "triggerAfterCharacters": 1,
    "maximumItems": 100,
    "showPreview": true,
    "delayMilliseconds": 70,
    "qualifyObjectNames": false,
    "useSquareBrackets": false,
    "engine": "native",
    "suppressNativeIntelliSense": true
  },
  "preview": {
    "mode": "rightArrow",
    "delayMilliseconds": 220,
    "width": 620,
    "height": 420
  },
  "diagnosticsEnabled": false,
  "asyncCompletionProbe": false
}
```

設定採用同目錄暫存檔原子取代，避免 SSMS 中止時只寫入半份 JSON。

## 中繼資料載入策略

分四層按需載入，讓第一次按鍵的成本與資料庫大小脫鉤：

| 層 | 內容 | 何時載入 |
|---|---|---|
| 1 | 物件與結構描述名稱 | 第一次需要建議時，常駐快取 |
| 2 | 單一物件的欄位與參數 | 使用者選取該物件，或滑鼠停留後在背景補上 |
| 3 | 模組定義本文 | 需要顯示或展開 ALTER 時 |
| 4 | 索引與外來鍵 | 只有展開結構預覽時 |

第四層刻意不併進第二層：第二層在按鍵路徑上，使用者輸入 `a.` 要的是欄位清單，
為此每次多付兩次查詢並不值得。

快取以「正規化連線字串（排除認證欄位）＋資料庫名稱」為鍵，同一個資料庫
開多個查詢分頁只會查詢一次。中繼資料查詢一律另開連線，不會干擾使用者
正在執行的查詢或明確交易。

第一層過期時**先回傳舊的、同時在背景更新**，不讓使用者為了重新整理而等待。
物件清單過期幾分鐘的代價，遠低於每隔幾分鐘就有一次按鍵要等一輪資料庫查詢
——而那一輪還會擋在欄位建議的前面。只有完全沒有資料時才真的等。

第二層則會**預先載入**：每次開啟建議清單時，順便把敘述裡每一張資料表的欄位
在背景撈回來。使用者打完 `FROM PUBLISHER a` 之後才會按下 `a.`，那段時間足夠
把欄位準備好，按下點號時直接命中快取。

超過 200 毫秒的中繼資料操作一律寫進 `SqlAssist.log`，不必先打開詳細診斷：

```text
耗時 1840 ms：欄位建議 [dbo].[PUBLISHER]（第二層查詢資料庫）
耗時 2100 ms：建議清單（目標 Column，45 筆）
```

## 為什麼改用平台原生管線

SSMS 的 T-SQL IntelliSense 是舊版語言服務，官方文件沒有說明新版 async completion
API 對 `ContentType "SQL"` 是否生效，因此先以探測量測，再決定架構。

實機量測（SSMS 22.9.12105.275）：

```text
非同步 IntelliSense 支援狀態：SQL → False
探測：平台已索取非同步建議來源，Provider 有被掃描到
探測：InitializeCompletion 首次被呼叫（觸發：Insertion 's'）
```

平台確實會把按鍵路由進非同步完成管線。`IsCompletionSupported` 回報的 False 是
時序造成的假訊號：那一次查詢發生在 TextView 建立的當下，此時本擴充的建議來源
還沒被實例化，broker 自然找不到任何對應 `ContentType "SQL"` 的來源。

自製 WPF 清單有三個無法靠修補解決的問題，全都源自「在編輯器外面自己畫一個視窗」：
與 SSMS 內建清單同時出現、只能用鍵盤操作、以及必須反覆呼叫 `DismissAllSessions`
去搶 session。改用原生管線後三者一併消失。

實作分成三個 MEF 匯出：

| 匯出 | 負責 |
|---|---|
| `IAsyncCompletionSource` | 提供項目、右側說明面板 |
| `IAsyncCompletionItemManager` | 排名、篩選與命中標示 |
| `IAsyncCompletionCommitManager` | 接續建議與 ALTER 展開的提交行為 |

排名器不能省：平台預設的比對器沒有詞首感知，少了它 `libr` 又會排不到 `Lib_Reader`。

「工具 → 選項 → SqlAssist → 一般 → 非同步建議追蹤」會把管線的每一步寫進
`SqlAssist.log`，用於疑難排解。

## 環境需求

- Windows x64
- SQL Server Management Studio 22.9.x
- Visual Studio 18／2026 或相容的 MSBuild
- .NET SDK 10.0.400

## 建置與測試

```powershell
Set-Location 'D:\GitProject\SqlAssist.Ssms22'
.\tools\Run-CoreTests.ps1
.\tools\Build-Extension.ps1
```

預期輸出：

```text
src\SqlAssist.Ssms22\bin\Release\net48\SqlAssist.Ssms22.vsix
```

測試執行器由 `global.json` 的 `test.runner` 指定為 Microsoft.Testing.Platform
（.NET 10 SDK 不再支援 VSTest 轉接層）。

## 安裝

先關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Install-Extension.ps1
```

安裝程式會開啟 SSMS 隨附的 `VSIXInstaller.exe`，請在畫面中確認安裝目標為
**SQL Server Management Studio 22**。安裝後重新啟動 SSMS。

## 解除安裝

先儲存查詢並關閉所有 SSMS 視窗，再執行：

```powershell
.\tools\Uninstall-Extension.ps1
```

預設會顯示 VSIXInstaller 確認介面，如需無介面模式加上 `-Quiet`。
解除安裝只移除 VSIX，會保留 `%LOCALAPPDATA%\SqlAssist.Ssms22` 內的設定與診斷紀錄。

## 診斷

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\SqlAssist.log
```

```powershell
.\tools\Show-Diagnostics.ps1
```

## 目前限制

- 建議項還沒有圖示。原生清單支援 `ImageElement`，只是尚未挑選 moniker。
- 未限定的欄位建議不分子句：`GROUP BY` 之後與 `SELECT` 之後給的是同一份清單。
- 尚未依外部索引鍵補完 JOIN 條件。
- 尚未支援暫存表、資料表變數、CTE 名稱與跨資料庫參考的欄位。
- 尚未實作結果格的 `Script as INSERT`、`Copy as IN clause`。
- Snippet 仍為內建清單，尚未支援使用者自訂。
- SSMS 目前不正式支援第三方擴充套件，安裝與載入方式需要以實機驗證。

## 下一階段

1. 依外部索引鍵補完 JOIN 條件。
2. 依子句細分未限定的欄位建議（`GROUP BY` 之後不該出現不可分組的欄位）。
3. 建議項圖示與篩選列（只看資料表、只看欄位）。
4. 使用者可編輯的 Snippet 管理器，支援佔位符。
5. 結果格的 `Script as INSERT`、`Copy as IN clause`。
