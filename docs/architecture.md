# 架構

## 三個專案

| 專案 | 目標框架 | 相依 | 職責 |
|---|---|---|---|
| `SqlAssist.Core` | netstandard2.0 | 無 | 詞法、剖析、排名、設定模型——純文字進、純結果出 |
| `SqlAssist.Metadata` | netstandard2.0 | `System.Data` | 資料庫中繼資料的查詢、模型與快取 |
| `SqlAssist.Ssms22` | net48 | SSMS 組件 | VSIX：MEF 匯出、命令、視窗、Unified Settings 接線 |

分層的判準只有一條：**這段邏輯需不需要 SSMS 才跑得起來**。不需要就放 Core 或
Metadata，因為那兩個專案跑得起單元測試；需要就放 Ssms22，而且要薄到只剩接線。

`SqlCompletionTriggers`（要不要重開清單）是這條界線的樣板：它只看文字與游標位置，
所以整組情境都測得到；Ssms22 那一側只負責在對的時機呼叫它，並把結果變成
`OpenOrUpdate`。同樣地，設定的 moniker 對應、列舉解析與數值收斂全在
`Core/Settings/SqlAssistSettingsReader`，Ssms22 只把 `ISettingsReader` 包成
`ISettingValueSource`。

## 資料夾即命名空間

`src/SqlAssist.Core/Settings/` 對應 `SqlAssist.Core.Settings`，測試專案鏡像同一份
路徑。新增檔案照這個規則放，不需要改任何 `.csproj`（SDK 樣式專案自動納入）。

```text
src/SqlAssist.Core           純邏輯，可完整單元測試
  Settings/                  設定模型、moniker、註冊值到強型別快照的對應
  Completion/                建議項、上下文分析、觸發條件與排名
  Keywords/                  關鍵字與內建函式目錄、大小寫改寫、位置判斷
  Parsing/                   詞法器、語彙狀態、語句範圍模型、文字來源介面
  Wildcards/                 SELECT * 的來源分析與展開後的欄位排版
  Matching/                  與領域無關的詞首感知模糊比對
  Snippets/                  片段模型、佔位符推導、展開與 JSON 序列化
  Json/                      最小的 JSON 讀寫器（Core 零相依，不引 System.Text.Json）
  IsExternalInit.cs          netstandard2.0 用 init 存取子的編譯器墊片

src/SqlAssist.Metadata       只依賴 System.Data
  Model/                     物件、欄位、參數、索引與外鍵的唯讀模型
  Querying/                  連線來源、目錄查詢語句與資料列對應
  Caching/                   四層按需載入的快取與連線層級的登錄
  Formatting/                識別字括號化與型別字串格式化

src/SqlAssist.Ssms22         net48 VSIX
  Completion/                平台非同步 IntelliSense 的來源、排名器與提交管理員
  Wildcards/                 Tab 展開萬用字元的命令處理常式與提示
  QuickInfo/                 滑鼠停留的物件結構提示
  Preview/                   浮動結構預覽視窗
  Snippets/                  片段檔的讀寫與管理介面
  Settings/                  Unified Settings 服務的接線與快取
  Commands/                  工具選單的命令與命令識別碼
  Editor/                    文字檢視接線、游標處物件定位、輸入時的關鍵字大寫
  Connections/               SSMS 連線的取得，以及依連線提供中繼資料
  UI/                        所有自建介面的唯一外觀來源
  SqlAssistPackage.cs        套件進入點
  SqlAssistRuntimeState.cs   跨功能的執行期狀態
  SqlAssistDiagnostics.cs    診斷紀錄
```

三條規則：

- **不為單一檔案開資料夾。** 只有一個成員的分類不是分類，是誤導。
- **不製造循環相依。** `Matching` 不認識 `Completion`——它是與領域無關的字串比對，
  `SuggestionMatch` 因此放在 `Completion` 而不是 `Matching`。
- **不用相對命名空間限定。** `Metadata.SqlObjectInfo` 這種寫法在
  `SqlAssist.Ssms22.*` 底下會解析到別的地方，一律 `using` 加簡名。

## 為什麼改用平台原生管線

SSMS 的 T-SQL IntelliSense 是舊版語言服務，官方文件沒有說明新版 async completion
API 對 `ContentType "SQL"` 是否生效，因此先以探測量測，再決定架構。

實機量測（SSMS 22.9.12105.275）確認平台確實會把按鍵路由進非同步完成管線：
`GetOrCreate` 有被呼叫、`InitializeCompletion` 也在第一次輸入時就進來了。
唯一看起來相反的訊號是 `IAsyncCompletionBroker.IsCompletionSupported` 對
`ContentType "SQL"` 回報 `False`，那是時序造成的假訊號——那一次查詢發生在
TextView 建立的當下，此時本擴充的建議來源還沒被實例化，broker 自然找不到
任何對應的來源。量測完成後那組探測程式碼已經移除。

自製 WPF 清單有三個無法靠修補解決的問題，全都源自「在編輯器外面自己畫一個視窗」：
與 SSMS 內建清單同時出現、只能用鍵盤操作、以及必須反覆呼叫 `DismissAllSessions`
去搶 session。改用原生管線後三者一併消失，自製清單那條路徑已於 0.13.0 移除。

實作分成三個 MEF 匯出：

| 匯出 | 負責 |
|---|---|
| `IAsyncCompletionSource` | 提供項目、右側說明面板 |
| `IAsyncCompletionItemManager` | 排名、篩選與命中標示 |
| `IAsyncCompletionCommitManager` | 接續建議與 ALTER 展開的提交行為 |

排名器不能省：平台預設的比對器沒有詞首感知，少了它 `libr` 又會排不到 `Lib_Reader`。

「設定 → SqlAssist → 診斷 → 寫入詳細診斷紀錄」會把逐次建議與提交的細節寫進
`SqlAssist.log`，用於疑難排解。
