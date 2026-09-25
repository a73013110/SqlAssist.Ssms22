# 在地化

本頁包含介面文字的來源、哪些不翻、語言切換與遷移流程；固定譯名與英文文風見
[術語表](localization-glossary.md)。

## 文字從哪裡來

- 使用者看得到的文字一律寫在 `<類別>.<語言>.resjson`，與用它的程式碼放同一個資料夾。
  `zh-Hant` 是來源語言，決定有哪些鍵與佔位符；檔案是扁平的「鍵 → 文字」，可寫 `//` 註解給譯者。
- `tools/SqlAssist.TextGenerator` 產生同名的 public static class，命名空間由 `src/<專案>/<資料夾>`
  推出。沒有佔位符的是屬性，有的是方法：`UpdateText.Available(latest, current)`。
- 佔位符一律具名（`{count}`、`{size:N0}`），譯文可以調換語序；不收 `{0}`。英文單複數用句型避開，
  避不開就拆兩個鍵，不在程式裡拼字尾，也不拿數字接量詞（`{count} 個` 整句翻）。
- 數字依介面語言格式化：句子裡的寫成佔位符格式，不在句子裡的（統計格、徽章）用 `SqlText.Number`；
  不用 `CultureInfo.CurrentCulture`，那是 SSMS 的文化，可以與介面語言不同。
- 各語言的鍵、佔位符不一致、缺檔，或非中日韓語言的譯文含中日韓字元（漏翻），都是建置錯誤
  （SQLTXT001–011），不在執行期退回來源語言。
- 不用 resx：衛星組件要靠 VSIX 探測路徑與隔離 AppDomain 各自載入，載不到只會安靜退回；
  產生器把所有語言編進同一個組件，參數個數也由編譯器檢查。XML 對 diff 與 AI 也都貴。
- 唯一例外是設定頁：註冊檔只能寫 `@鍵;{packageGuid}`，由 SSMS 到套件組件的資源查表。文字照樣寫在
  `Ssms22/Settings/SettingsPageText.<語言>.resjson`、照同一流程翻譯；檔案標 `SqlAssistTextResourceOnly`，
  產生器照同一套 SQLTXT 規則驗證但不產生類別。`SettingsPageText.targets` 在建置時把英文編成中性資源
  、其餘語言編成衛星組件，註冊檔引用不存在的鍵是 SQLSET006。衛星組件在
  Deploy 白名單裡，缺了只會安靜退回英文。
- 中性語言一律是英文：設定頁資源、命令表（`Menus.vsct`）與 vsixmanifest 都寫英文，繁中分別走衛星組件、
  `TextChanges` 與 `zh-Hant/Extension.vsixlangpack`。
- 語言清單只有根目錄 `Directory.Build.props` 的 `SqlAssistTextLanguages` 一份（逗號分隔）。
  新增語言：加在那裡、每份 `.resjson` 與資料型覆蓋檔補一份、語言設定加一個列舉值。
- 跨功能的共用詞（確定、取消、複製）放 `Core/Localization/CommonText`；SQL 物件與建議項目的種類名稱
  （資料表、預存程序、資料表提示……，含複數形）放同一資料夾的 `SqlKindText`。不在各功能各翻一次。

## 資料型文字

- `BuiltInDocs.json`、`DefaultSnippets.json` 這種一筆多欄、帶巢狀表的內嵌資源不進產生器：來源維持繁中，
  旁邊放同名覆蓋檔 `<名稱>.<語言>.json`，由 `Core/Localization/SqlTextOverlay` 依語言疊上去，缺的欄位用來源那一句。
- 格式是 `{ "<編號>": { "<欄位>": "譯文" } }`，只放含中文的文字欄；編號與欄位由載入器定，陣列位置從 0 起算：

  | 來源 | 編號 | 欄位 |
  |---|---|---|
  | `BuiltInDocs` 的 `docs` | `name` | `summary`、`example` |
  | `BuiltInDocs` 的 `tables` | `tables.<編號>` | `title`、`columns.<欄>`、`rows.<列>.<欄>` |
  | `DefaultSnippets` | `id` | `title`、`description`、`code`、`placeholders.<欄位 id>.tooltip`／`.default` |

- 範例與片段樣板只翻 `--` 註解與中文字串常值，`$surround$`、`{name}` 原樣保留；`SqlTextOverlayTests` 守編號、欄位與標記。
- 覆蓋檔內嵌時標 `WithCulture="false"`，否則 MSBuild 把 `.en.json` 當文化資源拆進衛星組件。
- 關鍵字目錄（`SqlDataTypeCatalog` 這幾個 `.cs`）的一行說明走 `.resjson` 而不是覆蓋檔：覆蓋檔要把中文來源留成
  字面值（SQLTXT100 擋），`.resjson` 的鍵由編譯器檢查。目錄存 `() => DataTypeText.Int`，取值時才讀語言。
- 提示視窗的截斷上限以字元計、不分語言，英文照同一個上限寫得精簡；`SqlBuiltInDocCatalogTests` 兩種語言都檢查。
  不改成依顯示寬度截斷：同一條路徑也截使用者的擴充屬性說明，放寬會連它一起放寬。

## 什麼不翻

- 診斷紀錄、平台防護的作業名稱與診斷報告：給維護者比對，固定繁中。在接收的參數或所在成員
  標 BCL 的 `[Localizable(false)]`（CA1303 同一套語意），SQLTXT100 就不檢查。
- 使用者資料（SQL Memory、收藏、自訂片段）與 SQL Server 回傳的訊息。未命名查詢視窗的預設文件名稱照擷取當下的
  語言存進 `Documents.DisplayName`，不改存代碼：SSMS 的查詢視窗一定帶標題，這條幾乎走不到，改存代碼卻要動讀取、
  名稱搜尋與排序。
- 設定鍵、moniker、列舉字面值與儲存格式：那是資料。
- 已經寫進編輯器的文字不回頭改；之後產生的指令碼註解用當下的語言。

## 目前語言

- 取值走 `Core/Localization/SqlText.Current`。**不動** `Thread.CurrentUICulture`：那是 SSMS 的狀態。
- 設定 `sqlAssist.general.language`（`auto`／`zhHant`／`en`，字面值是語言名稱去掉連字號）。`auto` 由
  `SqlLanguage.Match` 依宿主介面文化挑：zh-* 用繁中，其餘用英文。宿主文化只在 UI 執行緒問得到
  （`IUIHostLocale`），`Ssms22/Settings/SqlLanguageSwitch` 在設定接上時問一次記下來。
- 設定生效前是來源語言，所以既有測試的中文斷言不必改。固定語言用 `SqlText.Use(...)`
  （AsyncLocal，平行測試互不干擾）；會切換全域語言的測試放進不平行的集合。
- 隔離 AppDomain（SQL Memory 儲存）有自己一份 `SqlText.Current`，不跟著宿主切換。隔離側只回分類、原因碼與
  錯誤碼，例外訊息當診斷（`SqlMemoryStorageException` 的訊息參數標了 `[Localizable(false)]`）；
  給使用者的句子由宿主的 `SqlMemoryTimeText.Describe` 組。

## 即時切換

設定變更時 `SqlAssistSettingsStore.Reload` 呼叫 `SqlText.SetLanguage`。`SqlText.Changed` 在呼叫端的執行緒上發出；
介面改接 `SqlLanguageSwitch.Changed`：保證在 UI 執行緒、每個處理常式各自走 Guard，訂閱者關閉時要解除訂閱。

| 表面 | 切換後 |
|---|---|
| 每次才產生的文字（補全、提示、通知、指令碼） | 下一次就是新語言 |
| 含譯文的快取與不可變物件（補全篩選鈕、圖示朗讀名稱） | 以語言為鍵各留一份（`SqlLanguageCache`）：平台以實體比對，不能每次新建 |
| 載入時合併的資料（內建片段） | 記著合併時的語言，不同就重新合併 |
| 工具窗（SQL Search、SQL Memory） | 整份內容重建，帶著搜尋字串與分頁；建構時取字的元件一起換 |
| 浮動結構預覽 | 關掉並丟掉建好的視窗，下次展開重建 |
| 區塊摺疊提示、邊欄說明、跨頁提示 | 丟掉快取重發 `TagsChanged`；跨頁提示忘掉顯示過的那一組 |
| 已顯示的通知 | 重新投影：標題、狀態與按鈕換；各功能建立時組好的訊息留在舊語言直到到期 |
| 選單命令 | 命令表每顆標 `TextChanges`，QueryStatus 設 `Text`；`Test-CommandTable.ps1` 核對 |
| 字型與色彩的分類名稱（`ClassificationFormatDefinition`） | MEF 建立時定字，重新啟動 SSMS 才換 |
| 強制回應對話框 | 開著時進不了設定，不處理 |
| 設定頁、擴充功能清單、鍵盤頁的命令名稱 | 跟隨 SSMS 介面語言，本設定管不到：設定頁走 `@key;{packageGuid}` 資源，清單走 `zh-Hant/Extension.vsixlangpack`，命令名稱（`LocCanonicalName`）只有英文 |

## 新增介面文字（省 token 的做法）

SQLTXT100 全面開著，沒有暫時豁免的資料夾；只進紀錄的文字在接收端標 `[Localizable(false)]`。

1. 先查 `CommonText`／`SqlKindText` 有沒有同一句；沒有才在用它的資料夾找現有的 `<類別>.zh-Hant.resjson`，再沒有才新增一組。
2. 建置那個專案。SQLTXT100 列出的檔案、行號與前 24 字就是漏網的字面值，只讀命中行附近，不必整檔讀。
   Core、Metadata、Sqlite 用 `dotnet build src/<專案> -c Release`；Ssms22 走 `tools/Build-Extension.ps1`。
3. 文字寫進 `<類別>.zh-Hant.resjson`（鍵用 PascalCase），呼叫端改用產生的成員，拼接字串改成
   一整句具名佔位符；只進紀錄的改標 `[Localizable(false)]`。
4. 翻譯交給 `.claude/agents/resjson-translator.md`（Haiku，規則寫在定義裡）：一個區塊只呼叫一次，
   只傳全部 zh-Hant 檔路徑；回來只審譯文。沒有這個代理的工具照術語表手動翻。
5. 建置＋測試；每個區塊至少補一個 `SqlText.Use` 英文斷言。建構時就取字的介面元件要照「即時切換」那張表接上。
