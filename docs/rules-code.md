# 程式碼、測試與工具護欄

修改 `.cs`、專案檔、測試或 `tools/` 前必讀。

## 分層

- **禁止** Core 或 Metadata 參照 Visual Studio／SSMS 組件。Metadata 只依賴 `System.Data`。
- **禁止**把只看文字即可判斷的邏輯放在 Ssms22；那層只拿服務、接事件及寫回編輯器。
- **禁止** `Core/Matching` 參照 `Core/Completion`；Matching 必須與領域無關。

## 路徑與名稱

- 資料夾與命名空間一致；測試鏡像來源路徑。不要為單一檔案建立資料夾。
- 不用 `Metadata.SqlObjectInfo` 這類相對限定；用 `using` 加簡名。
- 禁止手改 `Keywords/SqlKeywordCatalog.Generated.cs`；改 `tools/Generate-Keywords.ps1` 後重跑。

## 對 SSMS 組件的參考

- 只有 `Microsoft.VisualStudio.*` 與 SSMS 專有組件可以用 `HintPath` 指向安裝目錄。
- `System.*` 這類 BCL 外掛組件一律走 NuGet 的契約版本，並加 `ExcludeAssets="runtime"`
  讓它不進 VSIX。抓安裝目錄那份會把建置機的修補版本（例如 `10.0.0.10`）寫進參考，
  而 `Ssms.exe.config` 的 bindingRedirect 只涵蓋到宿主自己那份；裝到較舊的 SSMS 上
  就是 `FileNotFoundException`，沒有降級，只有用到的功能整組消失。
- `tools/Test-VsixPackage.ps1` 會擋下版本不是 `x.0.0.0` 的參考。

## 品質與公開內容

- `TreatWarningsAsErrors` 與 Nullable 必須維持啟用。SSMS 更新換掉參考組件的註解時，
  先照新契約改寫，`!` 與 `#pragma` 是最後手段且要寫明理由，見[開發](development.md)。
- 測試使用 Microsoft.Testing.Platform；執行 `tools/Run-CoreTests.ps1` 或 `dotnet test <方案>`，
  不得加回 VSTest 轉接層。
- 註解只寫理由、失敗方案或不照做的症狀，不逐行翻譯程式碼。
- 公開 repo 禁止出現真實系統的 schema、資料表、欄位或程序名。測試與文件只用既有的
  圖書館領域：`Lib_Reader`／`Lib_Tag`、`PUBLISHER`／`PUBL_CODE`、
  `Cat_BookCopy`／`CopyNo`、`Loan`／`LoanDetail`／`Copy`／`Branch`；例外只有 T-SQL
  保留字案例與產品內建捷徑。
- 工具不得寫死 SSMS 路徑或擴充 Identity Id；從 `tools/SqlAssist.Tools.psm1` 取得，並支援
  `-SsmsInstallDir` 覆寫。

## 文字格式

直接保留 LF 與 UTF-8 無 BOM，不在收尾時批次「修復」換行或重寫無關檔案。原始診斷只放
被忽略的 `artifacts/` 並保留原格式。完成前執行 `tools/Check-TextFiles.ps1`。
