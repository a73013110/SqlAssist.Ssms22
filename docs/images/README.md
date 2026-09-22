# README 與發布圖片

本頁定義 README 圖片規則與操作 GIF 重製流程；靜態品牌圖的模型輸入見[生成提示詞](prompts.md)，
網站建置與可刪圖片見[網站發布](../website.md)。

## 加圖規則

- README 顯示寬度：主視覺 900、單欄內容圖 820、雙欄縮圖 400；不可用原尺寸撐開頁面。
  首頁需直接展示補全、SQL 展開、結構預覽與結果格線，不可只留文字摘要。
- PNG 加入前先壓縮；Git 會保留每版二進位內容。
- 實機畫面只用[允許的虛構圖書館名稱](../rules-code.md)，並檢查連線、資料庫與登入名稱。
  新圖優先沿用 `LibraryDB` 與 `Lib_Reader` 系列。
- 內容圖要有可獨立理解的 `alt`；必要說明留在 Markdown，不烙進圖片，以便翻譯與輔助工具讀取。
- 正式截圖的外框、主題與控制項必須來自目前支援的 SSMS 22。

## GIF 操作示意與重製

`*-demo.gif` 以 HTML／CSS 重建新版 SSMS 22 操作，**不是實機錄影或效能證據**；README 必須明示。
素材只保留與操作相關的區域，使用虛構的 `LibraryServer`／`LibraryDB` 與命名護欄允許的物件；
禁止把參考截圖、真實連線、登入或物件名稱放進素材與腳本。

場景的單一真相來源是 `docs/demos/feature-demos.html` 及同目錄的資料、JS、CSS；不要在本頁重列
每段操作。瀏覽器版可播放、暫停與重播。GIF 無法暫停，因此 README 需保留文字步驟與同名 PNG 連結。

F12、包夾、INSERT、IN 的 SQL 由[產生器](../../tools/Generate-FeatureDemoData.cs)呼叫產品純邏輯，
輸出 `docs/demos/feature-demo-data.js`；該檔不可手改，也不連資料庫。產生結果不能取代 SSMS
右鍵選單與 COM 整合的實機驗收。

在專案根目錄依序執行：

```powershell
dotnet run --file tools/Generate-FeatureDemoData.cs
node tools/Render-FeatureDemos.mjs
python tools/Encode-FeatureDemos.py
```

需要 .NET SDK 10、Node.js、Playwright／Chromium、Python 3 與 Pillow；額外參數直接查各腳本說明。
驗收畫格只輸出到 `artifacts/feature-demos/`，Git 只收來源與最終 GIF／PNG。
自動檢查負責溢位、尺寸、循環、總時間與 2 MB 上限；發布前仍要目視首尾、點擊位置、SQL 正確性，
以及縮至 820 px 後的可讀性。

## 特殊檔案

`social-preview.png` 不放 README；到 GitHub 的 **Settings → General → Social preview** 上傳。

`SqlAssist.Icon.512.png` 同時供 VSIX manifest 的 `<Icon>` 與 `<PreviewImage>` 使用；專案以 `Link`
放入 VSIX 根目錄，由殼層縮放，不另維護縮圖。四角必須透明且無暗邊，以適用 SSMS 深淺主題。
`SqlAssist.ico` 供網站 Favicon 與 Windows 桌面場景共用。
