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
  Parsing/                   詞法器、語彙狀態、語句範圍模型、欄位來源解析、文字來源介面
  Wildcards/                 SELECT * 的星號判定與展開後的欄位排版
  Preview/                   預覽的矩形定位、避障與雙側縮放純邏輯
  Matching/                  與領域無關的詞首感知模糊比對
  Snippets/                  片段模型、佔位符推導、展開與 JSON 序列化
  Json/                      最小的 JSON 讀寫器（Core 零相依，不引 System.Text.Json）
  IsExternalInit.cs          netstandard2.0 用 init 存取子的編譯器墊片

src/SqlAssist.Metadata       只依賴 System.Data
  Model/                     物件、欄位、參數、索引與外鍵的唯讀模型
  Querying/                  連線來源、目錄查詢語句與資料列對應
  Caching/                   四層按需載入的快取與連線層級的登錄
  Formatting/                識別字括號化、型別字串格式化、欄位性質的顯示語意

src/SqlAssist.Ssms22         net48 VSIX
  Completion/                平台非同步 IntelliSense 的來源、排名器與提交管理員
  Wildcards/                 Tab 展開萬用字元的命令處理常式與提示
  QuickInfo/                 滑鼠停留的物件結構提示
  Preview/                   浮動結構預覽視窗
  Snippets/                  片段檔、管理介面與 SSMS 原生 Expansion 接線
  Settings/                  Unified Settings 服務的接線與快取
  Commands/                  工具選單的命令與命令識別碼
  Editor/                    文字檢視接線、游標處物件定位、輸入時的關鍵字大寫
  Connections/               SSMS 連線的取得，以及依連線提供中繼資料
  UI/                        所有自建介面的唯一外觀來源
  SqlAssistPackage.cs        套件進入點
  SqlAssistRuntimeState.cs   跨功能的執行期狀態
  SqlAssistDiagnostics.cs    診斷紀錄
  SqlAssistPlatformGuard.cs  平台邊界的例外收斂
```

三條規則：

- **不為單一檔案開資料夾。** 只有一個成員的分類不是分類，是誤導。
- **不製造循環相依。** `Matching` 不認識 `Completion`——它是與領域無關的字串比對，
  `SuggestionMatch` 因此放在 `Completion` 而不是 `Matching`。
- **不用相對命名空間限定。** `Metadata.SqlObjectInfo` 這種寫法在
  `SqlAssist.Ssms22.*` 底下會解析到別的地方，一律 `using` 加簡名。

## 兩個以上的功能共用的東西

同一件事在兩個地方各寫一次時，痛的不是重複，是「其中一份改了另一份沒改」
不會有任何徵兆。以下每一個都是為了讓那種分岔變成不可能：

| 元件 | 共用什麼 | 分岔的症狀 |
|---|---|---|
| `Core/Parsing/SqlTrivia` | 空白與註解的略過 | 巢狀區塊註解在 tokenizer 是對的，在 ALTER 改寫卻停在內層結尾 |
| `Core/Parsing/SqlTokenNavigator` | 括號配對、跳過，以及「這個括號是不是子查詢」 | 括號不成對時，Scope 與萬用字元對同一段文字給出不同判斷；認得的開頭關鍵字分岔時，範圍分析與位置分析對同一個括號給出不同答案 |
| `Core/Parsing/SqlColumnSourceResolver` | 別名指向哪些欄位 | 同一個衍生資料表，`a.*` 展得開、`a.` 卻一個建議都沒有 |
| `Metadata/Formatting/SqlColumnPresentation` | 欄位性質與它的名稱 | 新增一種性質，某個表面就是少標一項 |
| `Ssms22/Editor/TextViewEditCoordinator` | 非同步替換文字的那道防線 | 覆蓋掉使用者在等待期間打的字 |
| `Ssms22/Editor/SnapshotNewLine` | 寫回去的多行文字用哪一種換行 | 同一份指令碼混進兩種換行，下一次 diff 整段變紅 |
| `Ssms22/Completion/SqlCommitExpander` | 提交後把整句換掉的流程（ALTER 定義、INSERT 骨架、EXEC 呼叫） | 三種展開裡有一種少了一道守門，那一種會蓋到別人的語句 |
| `Core/Snippets/SqlSnippetExpansion` | Snippet 的純文字、游標、欄位位置與錢字號規則 | caret fallback 與原生 XML 對同一段程式碼產生不同結果 |
| `Ssms22/SqlLanguageService` | SQL 語言服務 GUID | 內建清單偏好與原生 Snippet 各自連到不同語言服務 |
| `Ssms22/Editor/SqlObjectLocator` | 位置到物件的解析 | 滑鼠提示與結構面板對同一個位置給出不同答案 |
| `Ssms22/SqlAssistPlatformGuard` | 平台邊界的例外收斂 | 忘記收斂的 handler 讓輸入中斷或跳出錯誤對話框 |
| `Ssms22/UI/SqlAssistChrome` | 所有自建介面的外觀 | 兩個視窗長得像但又不完全一樣 |
| `tools/SqlAssist.Tools.psm1` | SSMS 路徑、擴充 Id、安裝探索 | 「安裝成功但部署說找不到」 |

刻意**不**共用的：`SqlColumnInfo.ToScriptLine` 與 `SqlObjectStructure` 的欄位定義
長得很像，但一個是給人看的單行描述、另一個要能貼進查詢視窗執行，合併之後每個
呼叫端都得傳一堆開關。

### 平台邊界的三族

`SqlAssistPlatformGuard` 的方法分成三族，選錯的症狀各不相同：

| 方法 | 用在哪 | 失敗時 |
|---|---|---|
| `Run`／`RunAsync`／`Create` | MEF 建立方法、按鍵與編輯器事件、派送佇列上的工作 | `WriteAlways` 完整堆疊，回傳替代值 |
| `Probe` | 向平台問一件可有可無的事：佈景筆刷、DPI、游標位置、錨點座標 | 只在詳細診斷打開時記一行 |
| `Begin`／`BeginProbe` | 沒有人接結果的背景工作；`BeginProbe` 是預先載入與預熱那一類 | 同上兩族的層級 |

`Probe` 這一族存在的理由是紀錄檔的訊噪比：這些呼叫在連線斷掉或版面還沒完成時
會**連續**失敗，用 `Run` 記的話真正的錯誤就埋在裡面找不到了。

取消一律當成正常結束，唯一的例外是 `RunPropagatingCancellation`：平台靠回傳的
Task 是不是取消狀態判斷結果作廢，吞掉取消再交出替代值等於把過期的內容當成有效答案。
它的替代值是 `Func<T>` 而不是值——那一族的替代值本身就要走一趟完整清單，
傳值的話每一次成功也要先付一次，也就是使用者每按一個鍵就白付一次。

**不**走 `SqlAssistPlatformGuard` 的四種情形，每一處都在程式碼裡註明了理由：

- 失敗要讓使用者看見：工具選單的命令、預覽視窗的狀態列、Snippet 管理員。
  Guard 的意思是「這一輪安靜地什麼都不做」，但使用者是自己按下去的，
  什麼都沒發生等於故障。
- 記錄後重擲：`SqlAssistPackage` 的載入失敗，殼層要靠例外知道套件沒載入成功。
- 有例外篩選的預期失敗：`SqlSnippetStore` 只接檔案系統錯誤，序列化的程式錯誤
  該讓它浮出來。
- `SqlAssistDiagnostics` 自己：Guard 失敗時要寫紀錄，而那裡正是紀錄本身。

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
| `IAsyncCompletionCommitManager` | 接續建議，以及 ALTER／INSERT／EXEC 三種提交後展開整句的行為 |

排名器不能省：平台預設的比對器沒有詞首感知，少了它 `libr` 又會排不到 `Lib_Reader`。

「設定 → SqlAssist → 診斷 → 寫入詳細診斷紀錄」會把逐次建議與提交的細節寫進
`SqlAssist.log`，用於疑難排解。
