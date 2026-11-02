# 頁籤依資料庫著色

本頁包含查詢視窗頁籤依連線著色的機制與判準；連線事件本身見[連線](metadata-connection.md)。

## 入口與開關

頁籤右鍵（或頁籤列右上角的設定）→ **文件索引標籤著色方式 → 資料庫**。
開關、配色與「選取索引標籤色彩」都是殼層的，所以不另立設定；SqlAssist 總開關關閉時不著色。
選過之後重開 SSMS 會自動還原。

## 機制

- `Ssms22/Connections/SqlDatabaseTabGroupProvider.cs` 掛在殼層的 `FileColor` 服務上：pkgdef 寫 `FileGroupProviders`（`ProvideFileGroupProvider`），
  套件初始化時向 `SRegisterFileGroupProvider` 註冊工廠。不自己畫頁籤，否則要跟著殼層的佈景、
  多列與工具提示重做一遍，還關不掉。
- 註冊必須在 `InitializeAsync` 完成前做完：殼層 `LoadPackageAsync` 回來就取工廠，取不到就當成
  沒有這個著色方式，整組頁籤安靜地不著色。
- 選單列出的順序是提供者 GUID 的字典順序，文字照索引對應，所以 GUID 開頭刻意大於內建的
  規則運算式。
- 選單文字走 `SettingsPageText` 的 `DatabaseTabGroupProviderName`：殼層建選單時套件可能還沒載入，
  只能由 `@鍵` 查資源。

## 記住選擇

- 殼層的 `environment.tabs.documentTabs.colorization` 只收無著色與內建三種方式，選「資料庫」存進去的是
  無著色，啟動時還會非同步套回來，否則不必另外記。`SqlDatabaseTabChoiceMemory` 把選擇存進 `SqlAssistState`。
- 偏好變動一律由 `SqlDatabaseTabChoice.Next` 判斷：提供者換掉就忘記，提供者還是資料庫卻被關掉就重新套用。
- 「無著色」與殼層重播改的是同一個偏好，只能在命令執行前（DTE `BeforeExecute`）清掉記憶來分辨，
  否則使用者再也關不掉。取不到 DTE 時整件事不做。

## 分組

- 由 `Core/Connections/SqlDatabaseTabGroups.cs` 發編號。一組是「伺服器＋資料庫」，不分大小寫：同名資料庫在測試機與正式機要看得出差別。
- 編號依第一次見到的順序發，存在 `SqlAssistState`，只增不刪。殼層拿編號對 16 色取餘數，使用者
  改過的顏色也跟著編號存，因此前 16 個資料庫不撞色，第 17 個開始循環。
- 沒有連線的查詢視窗與其他文件回傳 `null`，殼層就不著色。

## 重算時機

- 連線事件與設定變更都讓殼層叫每個頁籤重問一次，不比對是否換了資料庫：比對要多記一份每個視窗
  的狀態，而識別碼會被重複使用，記錯的頁籤就停在舊顏色。每批 F5 多付的只有每個頁籤一次記憶體查詢。
- 每次都用 `SqlWindowConnections.ReadWindow` 直接問 SSMS，不讀事件累積的那份，否則事件接上時
  已連好的視窗沒有顏色。提供者建立時也接上連線事件，否則那些視窗之後換資料庫沒有人通知。
