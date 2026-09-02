# 移至定義

游標停在物件名稱上按 **F12**，SqlAssist 另開一個查詢視窗，裡面是那個物件可以直接
執行的定義，並沿用你目前的連線。

```sql
SET QUOTED_IDENTIFIER ON
SET ANSI_NULLS ON
GO
-- =============================================
-- Author:      
-- Create date: 
-- =============================================
ALTER PROCEDURE dbo.usp_Loan_Renew
    @LoanId int,
    @Days   int = 7
AS
BEGIN
    ...
END
GO
```

游標停在 `usp_Loan_Renew` 之後——那是讀一份定義的起點，也是接著要改參數時的位置。
停在整份的結尾等於一打開就被捲到最後一行。

「工具 → SqlAssist → 移至定義」是同一個功能的第二個入口，在沒有 SQL 查詢視窗時
會變灰。要換一個鍵請到「工具 → 選項 → 環境 → 鍵盤」，命令名稱是
`SqlAssist.移至定義`。

## 產生出來的是什麼

| 物件種類 | 內容 |
|---|---|
| 預存程序、函式、觸發程序、檢視 | `OBJECT_DEFINITION` 的原文，開頭的 `CREATE` 改寫成 `ALTER` |
| 資料表 | 重建的 `CREATE TABLE`，後面接索引與外來鍵 |
| 資料表型別 | 重建的 `CREATE TYPE ... AS TABLE`，主索引鍵寫成不具名的內嵌條件約束 |
| 取不到定義的模組 | 整段註解，寫明兩個可能的原因與查得到的欄位、參數 |
| 取不到欄位的資料表、資料表型別 | 整段註解，寫明兩個可能的原因 |
| 同義字、序列 | `CREATE SYNONYM`、`CREATE SEQUENCE`（維持 CREATE，這兩種沒有整體的 ALTER 寫法） |
| 認不出來的種類 | 整段註解，寫明為什麼組不出可執行的指令碼 |

前五列與浮動預覽的**指令碼**分頁是同一份（[structure-preview.md](structure-preview.md)），
差別只在這裡多包了批次樣板並把模組改成 `ALTER`。

開頭那兩個 `SET` 不是裝飾。`ALTER PROCEDURE` 必須是批次裡的第一個敘述，所以它們
後面一定要有 `GO` 才分得開；而計算欄位、篩選索引與索引檢視對這兩個選項的值有要求，
少了它們的 `CREATE TABLE` 在某些連線設定下會直接失敗。SSMS 自己的「編寫指令碼為」
也是照這三行開頭的。

### 為什麼是 ALTER 而不是 CREATE

F12 之後接著要做的事幾乎都是「改一下再執行」。給 `CREATE` 的話每一次都要自己把
第一個字改掉，而那正是提交建議時 `ap` 展開成完整 `ALTER` 定義已經在做的事
（[completion-commit-expansion.md](completion-commit-expansion.md)）——兩條路徑對同一個模組給出不同的開頭關鍵字，
只會讓人以為其中一條壞了。

資料表與資料表型別**不**改寫。`ALTER TABLE` 沒有對應的整體寫法，型別更是連
`ALTER TYPE` 都沒有；改下去得到的是一段執行到一半才失敗的指令碼。

資料表也不改寫成 `ALTER`，同義字與序列同理：`CREATE SYNONYM` 沒有整體的 `ALTER`
寫法，序列的 `ALTER SEQUENCE` 則改不了型別，貼上去只會在第一個子句就失敗。

### 為什麼還留著「整段註解」這一支

只剩 `SqlObjectKinds.FromSysObjectType` 認不出來的種類會走到那裡。這一支不能拿掉：
沒見過的型別代碼一律對應到 `Unknown`，而 SQL Server 的物件型別只會愈來愈多，
硬湊一份指令碼出來就是指令碼在說謊。

同義字、序列與資料表型別曾經都在這一列。前兩者的定義其實就是目錄檢視上的那幾個
欄位（`sys.synonyms.base_object_name`、`sys.sequences` 的界限與快取），
現在由 `Metadata/Formatting/SqlCatalogScript` 組回 `CREATE`，見
[metadata.md](metadata.md)。資料表型別則有欄位，不擋掉會被寫成 `CREATE TABLE`，
照著執行會多出一張同名的資料表——而它需要的 `CREATE TYPE ... AS TABLE` 查得到的
資料就組得出來，所以現在直接組，三種都不必再繞。

「哪一類寫得出可以執行的指令碼」由 `SqlObjectKinds.HasExecutableScript` 一份說了算，
這條路徑與浮動預覽的指令碼分頁共用。各留一份判斷的症狀已經發生過一次：同一個
資料表型別，F12 給註解，預覽卻給 `CREATE TABLE`。

種類過得了關、這一次的資料卻不齊時（模組沒有定義、資料表沒有欄位），
註解是 `SqlObjectStructure` 那一端換的，這裡原樣帶出來——那一份寫的是缺什麼與
為什麼，與上面「這一類物件本來就組不出來」是兩件事。判斷同樣只有一份，
見 [metadata.md](metadata.md)。

## 執行緒分工

這一條路徑要等一次資料庫查詢，而**等得起不等於可以在 UI 執行緒上等**。

| 階段 | 執行緒 | 做什麼 |
|---|---|---|
| 1 | UI（按鍵） | 只看游標**所在的那一行**，判斷這一次要不要接手 |
| 2 | 背景 | 整份快照取文字、解析物件、查結構、組指令碼 |
| 3 | UI | 建立查詢視窗並寫入 |

第 1 階段刻意只看一行：整份快照取文字是一次完整的字串複製，幾千行的指令碼就是
幾百 KB，在 F12 按下去的那一瞬間做等於畫面停一下。只看一行會比完整文字寬鬆
（跨行的區塊註解裡也會判成識別字），那是刻意的——這一關只決定「值不值得往背景送」，
真正的答案由第 2 階段用完整文字重算一次。寬鬆的代價是多一次背景查詢，
嚴格的代價是 F12 在該有反應的地方沒反應。

第 1 階段判斷不是識別字時回報**沒有處理**，F12 照常落回平台；判斷是識別字就接手，
之後的每一種失敗都在狀態列說明原因。這條路徑**不**走 `SqlAssistPlatformGuard`
的收斂——那一族的意思是「這一輪安靜地什麼都不做」，但使用者是自己按下 F12 的，
什麼都沒發生等於故障。

連按兩次 F12 不會開出兩個視窗：重入防護記在每個編輯器一份的
`SqlDefinitionOpener` 上。

## 怎麼開出查詢視窗

編輯器的公開 API 開不出「SSMS 的查詢視窗」——那是 SSMS 自己的文件類型，帶著連線、
資料庫下拉與執行查詢的能力；用 `IVsUIShellOpenDocument` 開一份 `.sql` 只會得到一個
沒有連線的文字編輯器。

因此走 `IScriptFactory`，也就是 SSMS 自己的「新增查詢」按鈕走的那一條：

```csharp
var factory = serviceProvider.GetService(typeof(IScriptFactory)) as IScriptFactory;
var active = factory.CurrentlyActiveWndConnectionInfo;
factory.CreateNewBlankScript(ScriptType.Sql, active.UIConnectionInfo, null);
```

三件事值得記下來，因為每一件都試錯過：

- **不需要 `ServiceCache`。** SSMS 的 `ServiceCache.ScriptFactory` 只是
  `Package.GetGlobalService(typeof(IScriptFactory))` 的包裝，而 `IScriptFactory`
  與 `ScriptType` 在 `SqlWorkbench.Interfaces` 裡都是公開型別。多參照一個
  `SqlPackageBase` 只為了取一個全域服務不值得。
- **一定要傳連線資訊。** 只傳 `ScriptType` 的那個多載是「新增查詢」（會跳連線對話框）
  走的，不是「新增查詢（沿用目前連線）」走的。後者的分支
  ——連線群組非空就傳整組、否則傳單一個——與 SSMS 自己的實作逐字一致；
  只傳第一個會讓多重伺服器連線的視窗安靜地少連幾台。
- **第三個參數傳 `null`。** 那是「直接沿用這條實際連線」。傳進去代表兩個視窗共用
  一個 SPID，一邊執行長查詢另一邊就卡住；傳 `null` 則是用同一組認證另開一條。

向 SSMS 詢問目前連線這件事，在 QuickInfo 路徑是**明令禁止**的——那個呼叫有 UI
執行緒相依性，忙的時候會直接變成打字延遲。這裡可以，因為它是使用者主動按的，
而且一輪只問一次。

### 怎麼拿到新視窗的編輯器

`CreateNewBlankScript` 回傳的是 SSMS 自己的文件檢視型別，從它身上拿不到
`IWpfTextView`。改用「開完之後誰是目前的編輯器」則是**猜的**：那個值同時被建立與
取得焦點兩件事寫入，開窗失敗時它仍然是**來源**視窗，而把指令碼寫進來源視窗就是
覆蓋使用者正在編輯的查詢。

所以改成明確擷取：`ActiveSqlEditor.CaptureCreated` 只認「這一次呼叫期間建立的那一個」，
沒有就是沒有。寫入再走
`TextViewEditCoordinator.InsertIntoBlank`，它的守門是**緩衝區必須還是空的**——
那是對應非同步替換那一道「原文還在原處」的同一件事。空白查詢視窗的樣板是一個
0 位元組的檔案，所以這一道平常永遠成立；它擋的是「拿到的不是剛開的那個視窗」。

## F12 怎麼接到的

**現代編輯器的 `ICommandHandler` 接不到 F12。** 這一點是實測出來的，不是推論：
處理常式建立了、MEF 也沒過期（紀錄檔有「SQL 編輯器已建立」），按 F12 之後紀錄檔
卻連一行都沒有。原因是現代管線只看得到「核心編輯器決定轉進來」的命令，而 SSMS 的
查詢視窗在核心編輯器外面還有自己的文件檢視與舊版語言服務，兩者都排在命令鏈更前面。

**而且 SSMS 22 根本沒有把 F12 綁在 `Edit.GoToDefinition` 上。** 這一點也是實測
出來的：濾鏡掛上之後從來沒有收到 `VSStd97/GotoDefn`，紀錄檔顯示命令是從命令表的
鍵繫結進來的。兩件事加起來，就是原本按 F12 完全沒有反應的完整原因。

因此現在是兩條路：

| 路徑 | 接得到的情況 | 紀錄檔那一行 |
|---|---|---|
| **命令表的全域 F12 鍵繫結**（`Menus.vsct`） | 預設；F12 實際走的就是這一條 | 移至定義命令抵達 SqlAssist（**命令表**） |
| `SqlShellCommandFilter`（`IVsTextView` 上的 `IOleCommandTarget` 濾鏡） | 使用者自己把 `Edit.GoToDefinition` 綁到某個鍵 | 移至定義命令抵達 SqlAssist（**殼層命令濾鏡**） |

兩者互斥——鍵繫結只會解析出一個命令——所以不會執行兩次。`SqlDefinitionOpener`
的重入防護是最後一道保險。

現代命令管線那一條**已經移除**：實測證明它接不到，而濾鏡永遠排在它前面，
留著就是一個永遠不會執行的 MEF 匯出。

鍵繫結用**全域**範圍而不是某個編輯器範圍：SSMS 的查詢視窗用的是它自己的編輯器
工廠，猜錯 GUID 的症狀是繫結安靜地不生效。全域一定註冊得上，代價是在物件總管、
結果格線上也綁到，所以命令自己在沒有 SQL 編輯器時回報停用——停用的命令不會被
派送，F12 在那些地方就照常落回殼層。

### 濾鏡為什麼一定要回報 supported ＋ enabled

`QueryStatus` 那一段不是形式。殼層在派送 `Exec` 之前會先問過整條命令鏈，
**沒有任何目標認領的命令就是停用的，而停用的命令連 `Exec` 都不會發出去**——
症狀正好是「按下去完全沒反應，紀錄檔也什麼都沒有」。

### 濾鏡在最熱的路徑上

`QueryStatus` 在每一次按鍵、每一次閒置與每一次開選單時都會被呼叫數十次，
`Exec` 則是每打一個字元一次。因此那個類別的規矩是：先比命令群組 GUID，
不相符**立刻**原封轉給下一個目標，中間不配置任何物件、不取設定以外的任何服務、
不記錄任何東西。多做一件事就是每個按鍵多付一次。

## 按了某個鍵卻沒反應時

打開 `sqlAssist.diagnostics.verboseLogging`，紀錄檔會出現兩類新的行。

**「殼層命令濾鏡已掛上」** — 沒有這一行就是濾鏡沒掛上（取不到 `IVsTextView`），
只剩另外兩條路。

**「未處理的殼層命令：VSStd97/GotoDefn(925)」** — 濾鏡看到、但本擴充沒有處理的
命令，**每一個只記第一次**。全部都記的話打字時的 `TYPECHAR` 會把紀錄檔灌爆，
完全不記就沒辦法回答「按下那個鍵到底送出了什麼命令」，而那正是這一類問題唯一
需要的資訊。

於是流程變成：按下那個鍵，看紀錄檔多了哪一行，要攔的就是它——把它的命令群組與
識別碼加進 `SqlShellCommandFilter` 即可。這條路以後接任何殼層命令都一樣走。

三種都沒有出現，才是「這個鍵根本沒有繫結」，那要在 `Menus.vsct` 自己綁。

> 紀錄檔連「SQL 編輯器已建立」都沒有，是 MEF 快取過期，與命令無關，
> 見 [development.md](development.md)。

## 改了命令表一定要重新安裝

新增命令、選單項目或鍵繫結之後，**只部署 DLL 是沒有用的**。命令表雖然編譯在
`SqlAssist.Ssms22.dll` 的資源裡，殼層卻是照 pkgdef 的 `Menus.ctmenu, N` 那個 N
決定要不要重讀；`Deploy-DebugExtension.ps1` 不會更新 pkgdef，清快取也救不了。
症狀是新的選單項目不出現、新綁的鍵完全沒反應，而且沒有任何錯誤。

兩件事因此綁在一起：

1. 改了 `Menus.vsct` 就把 `SqlAssistPackage` 的 `ProvideMenuResource` 版號加一。
2. 用 `Install-Extension.ps1` 重新安裝，不要用 `Deploy-DebugExtension.ps1`。

第 1 件會讓第 2 件變成強制：部署腳本會比對兩邊的 N，不一致就直接擋下來並要求重裝。

## 為什麼沒有專屬設定

`sqlAssist.general.enabled` 關掉時這個功能一起關掉，除此之外沒有旋鈕。多一個設定就要動
註冊檔、POCO、moniker 與讀取對應四處（[settings.md](settings.md)），而這個功能
沒有「有些人要、有些人不要」的分歧——它只在使用者主動按鍵時才發生，不按就完全
不存在。

## 程式碼

| 檔案 | 職責 |
|---|---|
| `Metadata/Formatting/SqlObjectScript.cs` | 批次樣板、`CREATE` → `ALTER`、換行統一、游標落點（純函式，有單元測試） |
| `Ssms22/Menus.vsct` | 全域 F12 鍵繫結與工具選單項目——F12 實際走的就是這一條 |
| `Ssms22/Editor/SqlShellCommandFilter.cs` | 命令鏈最前面的濾鏡：接 `Edit.GoToDefinition`，也是「按了沒反應」時唯一看得到命令的地方 |
| `Ssms22/Editor/SqlDefinitionOpener.cs` | 五個步驟的串接與執行緒分工 |
| `Ssms22/Connections/SsmsScriptWindow.cs` | 向 `IScriptFactory` 要一個沿用連線的空白查詢視窗 |
| `Ssms22/Editor/ActiveSqlEditor.cs` | `CaptureCreated`：取回這一次建立的編輯器 |
| `Ssms22/Editor/TextViewEditCoordinator.cs` | `InsertIntoBlank`：寫進空白緩衝區 |
| `Ssms22/SqlAssistStatusBar.cs` | 進度與失敗的唯一回饋管道 |
| `Ssms22/Commands/SqlAssistCommands.cs` | 工具選單的第二個入口 |
