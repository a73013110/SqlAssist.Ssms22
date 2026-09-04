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

產生內容與不可執行時的降級規則見[F12 物件指令碼](definition-scripts.md)。

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
