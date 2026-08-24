# SqlAssist for SSMS 22

針對 SQL Server Management Studio 22.9.x 開發的 T-SQL 生產力擴充套件。
目前版本為 **0.6.0**。

## 專案結構

```text
src/SqlAssist.Core       netstandard2.0，無 Visual Studio 相依，可完整單元測試
  Matching/              詞首感知的模糊比對與命中區段
  Parsing/               T-SQL 詞法器與語句範圍模型（別名解析）
  （其餘）               語彙狀態、識別字解析、上下文分析、Snippet、設定

src/SqlAssist.Metadata   netstandard2.0，只依賴 System.Data
                         三層按需載入的資料庫中繼資料與快取

src/SqlAssist.Ssms22     net48 VSIX
  Completion/            非同步 IntelliSense 相容性探測
  QuickInfo/             滑鼠停留的物件結構提示
  Options/               工具→選項 的設定頁
```

核心邏輯刻意集中在沒有 Visual Studio 相依的兩個專案，因此排名、解析與
中繼資料對應都可以在不啟動 SSMS 的情況下驗證。目前共 273 項單元測試。

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

使用 `↑`、`↓` 選擇，`Tab` 或 `Enter` 提交，`Esc` 關閉。
清單顏色跟隨 SSMS 目前的佈景主題。

### 內建 Snippet

| 輸入 | 展開結果 | 接續行為 |
|---|---|---|
| `ssf` | `SELECT * FROM ` | 接著只顯示 Table／View |
| `ap` | `ALTER PROCEDURE ` | 接著只顯示 Procedure |
| `af` | `ALTER FUNCTION ` | 接著只顯示 Function |
| `select` | `SELECT` | — |
| `from` | `FROM` | — |

快捷詞不會在 SQL 字串、註解、雙引號識別字或方括號識別字內展開。

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

### 物件結構提示

滑鼠停留在資料表、檢視、預存程序或函式的名稱上，顯示欄位型別、NULL、PK、
IDENTITY、COMPUTED 與參數簽章。支援方括號、雙引號、結構描述限定與暫存表名稱。

停留在別名上會顯示它所指的資料表；停留在 `u.Name` 的欄位上則顯示該欄位的
型別與屬性。限定詞確實是資料來源但查無該欄位時不顯示提示，
不會退回去猜同名的資料庫物件。

## 設定

**工具 → 選項 → SqlAssist**，分為「一般」與「建議清單」兩頁，存檔後立即生效。
設定同時寫入下列檔案，兩邊看到的是同一份狀態：

```text
%LOCALAPPDATA%\SqlAssist.Ssms22\settings.json
```

**工具 → SqlAssist** 提供即時開關與動作：

```text
啟用 SqlAssist / 即時建議
Tab 快捷展開 / 關鍵字轉大寫 / Procedure／Function 選擇器 / 結果格命令
顯示診斷狀態 / 重新整理建議 / 設定… / 編輯 settings.json
非同步 IntelliSense 探測
```

全域開關快捷鍵：`Ctrl+Alt+Shift+S`

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
    "useSquareBrackets": false
  },
  "diagnosticsEnabled": false,
  "asyncCompletionProbe": false
}
```

設定採用同目錄暫存檔原子取代，避免 SSMS 中止時只寫入半份 JSON。

## 中繼資料載入策略

分三層按需載入，讓第一次按鍵的成本與資料庫大小脫鉤：

| 層 | 內容 | 何時載入 |
|---|---|---|
| 1 | 物件與結構描述名稱 | 第一次需要建議時，常駐快取 |
| 2 | 單一物件的欄位與參數 | 使用者選取或滑鼠停留在該物件時 |
| 3 | 模組定義本文 | 需要顯示或展開 ALTER 時 |

快取以「正規化連線字串（排除認證欄位）＋資料庫名稱」為鍵，同一個資料庫
開多個查詢分頁只會查詢一次。中繼資料查詢一律另開連線，不會干擾使用者
正在執行的查詢或明確交易。

## 非同步 IntelliSense 探測

自製 WPF 建議視窗長期應改用平台原生的 async completion。已確認 SSMS 22.9
隨附完整契約（`Microsoft.VisualStudio.Language.dll`），但 SSMS 的 T-SQL
IntelliSense 是舊版語言服務，新版 API 對 `ContentType "SQL"` 是否實際生效
需要實機量測，因此先量測再決定架構。

探測用的建議來源預設**不參與**完成流程，只記錄自己有沒有被呼叫，
SSMS 原生 IntelliSense 的行為不受影響。完整量測結果在「工具 → SqlAssist →
顯示診斷狀態」的「非同步 IntelliSense 探測」段落；其中兩個決定性事實
（Provider 是否被掃描到、`InitializeCompletion` 是否被呼叫）會在第一次發生時
直接寫進 `SqlAssist.log`，不必開對話框也看得到。

### 量測結果（SSMS 22.9.12105.275）

```text
非同步 IntelliSense 支援狀態：SQL → False
探測：平台已索取非同步建議來源，Provider 有被掃描到
探測：InitializeCompletion 首次被呼叫（觸發：Insertion 's'）
```

**平台確實會把按鍵路由進非同步完成管線**，因此原生 IntelliSense 是可行的方向。

`IsCompletionSupported` 的 False 是時序造成的假訊號，不是能力限制：那一次查詢
發生在 TextView 建立的當下，此時本擴充的建議來源還沒被實例化，
`IAsyncCompletionBroker` 自然找不到任何對應 `ContentType "SQL"` 的來源。

若要實際觀察清單外觀與 Tab 提交行為，可開啟「工具 → 選項 → SqlAssist →
一般 → 非同步 IntelliSense 探測」。開啟後可能與 SSMS 原生清單同時出現。

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

- 建議視窗仍為自製 WPF。探測已證實可改用平台原生 IntelliSense，尚未動工。
- 尚未建議未限定的欄位，例如 `SELECT |` 或 `WHERE |` 直接列出敘述內所有欄位。
- 尚未依外部索引鍵補完 JOIN 條件。
- 尚未支援暫存表、資料表變數、CTE 名稱與跨資料庫參考的欄位。
- 尚未實作結果格的 `Script as INSERT`、`Copy as IN clause`。
- Snippet 仍為內建清單，尚未支援使用者自訂。
- SSMS 目前不正式支援第三方擴充套件，安裝與載入方式需要以實機驗證。

## 下一階段

1. 未限定的欄位建議（`SELECT |`、`WHERE |` 列出範圍內所有欄位）。
2. 依外部索引鍵補完 JOIN 條件。
3. 改用平台原生 IntelliSense：取得原生的定位、螢幕邊界處理、篩選列與圖示，
   並且不必再主動關閉 SSMS 的原生建議清單。
4. 使用者可編輯的 Snippet 管理器，支援佔位符。
5. 結果格的 `Script as INSERT`、`Copy as IN clause`。
