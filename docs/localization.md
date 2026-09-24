# 在地化

本頁包含介面文字的來源、哪些不翻、語言切換與遷移流程；固定譯名與英文文風見
[術語表](localization-glossary.md)。

## 文字從哪裡來

- 使用者看得到的文字一律寫在 `<類別>.<語言>.resjson`，與用它的程式碼放同一個資料夾。
  `zh-Hant` 是來源語言，決定有哪些鍵與佔位符；檔案是扁平的「鍵 → 文字」，可寫 `//` 註解給譯者。
- `tools/SqlAssist.TextGenerator` 產生同名的 public static class，命名空間由 `src/<專案>/<資料夾>`
  推出。沒有佔位符的是屬性，有的是方法：`UpdateText.Available(latest, current)`。
- 佔位符一律具名（`{count}`、`{size:N0}`），譯文可以調換語序；不收 `{0}`。英文單複數用句型避開，
  避不開就拆兩個鍵，不在程式裡拼字尾。
- 各語言的鍵、佔位符不一致或缺檔都是建置錯誤（SQLTXT001–010），不在執行期退回來源語言。
- 不用 resx：衛星組件要靠 VSIX 探測路徑與隔離 AppDomain 各自載入，載不到只會安靜退回；
  產生器把所有語言編進同一個組件，參數個數也由編譯器檢查。XML 對 diff 與 AI 也都貴。
- 語言清單只有根目錄 `Directory.Build.props` 的 `SqlAssistTextLanguages` 一份（逗號分隔）。
  新增語言：加在那裡、每份 `.resjson` 補一份、語言設定加一個列舉值。
- 跨功能的共用詞（確定、取消、複製）放 `Core/Localization/CommonText`，不在各功能各翻一次。
- 資料型文字（`BuiltInDocs.json`、`DefaultSnippets.json`、關鍵字目錄的說明）不進產生器：
  另放同名語言覆蓋檔（`BuiltInDocs.en.json`），只含要翻的欄位，載入時依目前語言合併。

## 什麼不翻

- 診斷紀錄、平台防護的作業名稱與診斷報告：給維護者比對，固定繁中。在接收的參數或所在成員
  標 BCL 的 `[Localizable(false)]`（CA1303 同一套語意），SQLTXT100 就不檢查。
- 使用者資料（SQL Memory、收藏、自訂片段）與 SQL Server 回傳的訊息。
- 設定鍵、moniker、列舉字面值與儲存格式：那是資料。
- 已經寫進編輯器的文字不回頭改；之後產生的指令碼註解用當下的語言。

## 目前語言

- 取值走 `Core/Localization/SqlText.Current`。**不動** `Thread.CurrentUICulture`：那是 SSMS 的狀態。
- 「跟隨 SSMS」由 `SqlLanguage.Match` 依宿主介面文化挑：zh-* 用繁中，其餘用英文。
- 設定生效前是來源語言，所以既有測試的中文斷言不必改。固定語言用 `SqlText.Use(...)`
  （AsyncLocal，平行測試互不干擾）；會切換全域語言的測試放進不平行的集合。

## 即時切換

| 表面 | 切換後 |
|---|---|
| 每次才產生的文字（補全、提示、通知、指令碼） | 下一次就是新語言；含譯文的快取以語言為鍵，或在 `SqlText.Changed` 時清掉 |
| 工具窗（SQL Search、SQL Memory） | `Changed` 時重建內容，保留查詢字串這類輕量狀態 |
| 強制回應對話框 | 開著時進不了設定，不處理 |
| 選單命令 | 命令表標 `TextChanges`，在 QueryStatus 設 `Text` |
| 設定頁、vsixmanifest | 只能用 `@key;{packageGuid}` 資源跟隨 SSMS 介面語言，本設定管不到 |

語言設定最後才註冊：只翻了一半的設定屬於「按了只生效一部分」，[設定護欄](rules-settings.md)禁止。

## 遷移一個資料夾（省 token 的做法）

1. 從該資料夾 `.editorconfig` 的清單拿掉要處理的檔名；清空就刪檔。
2. 建置那個專案。SQLTXT100 列出的檔案、行號與前 24 字就是待辦，只讀命中行附近，不必整檔讀。
   Core、Metadata、Sqlite 用 `dotnet build src/<專案> -c Release`；Ssms22 走 `tools/Build-Extension.ps1`。
3. 文字寫進 `<類別>.zh-Hant.resjson`（鍵用 PascalCase），呼叫端改用產生的成員，拼接字串改成
   一整句具名佔位符；只進紀錄的改標 `[Localizable(false)]`。
4. 翻譯交給 `.claude/agents/resjson-translator.md`（Haiku，規則寫在定義裡）：一個區塊只呼叫一次，
   只傳全部 zh-Hant 檔路徑；回來只審譯文。沒有這個代理的工具照術語表手動翻。
5. 建置＋測試；每個區塊至少補一個 `SqlText.Use` 英文斷言。

## 尚未確認

- 設定頁的 `@key;{packageGuid}` 在 SSMS 22 是否依介面語言讀取衛星資源。
- 選單 `TextChanges` 在 SSMS 22 是否能即時改字。
