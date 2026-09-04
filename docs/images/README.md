# README 與發布圖片

## 現有資產

| 用途 | 檔案 |
|---|---|
| README | `hero.png`、`completion.png` |
| 語句展開示例 | `expand-star.png`、`expand-insert-into.png`、`expand-merge-into.png`、`expand-exec.png`、`expand-def-procedure.png` |
| 功能畫面 | `structure-preview.png`、`result-grid-utility.png` |
| GitHub 分享預覽 | `social-preview.png` |
| VSIX 圖示 | `logo.png` |

## 加圖規則

- README 顯示寬度：`hero.png` 900、單欄內容圖 820、同類功能的雙欄縮圖 400；不要用原尺寸
  撐開頁面。首頁應直接展示補全、SQL 展開、結構預覽與結果格線，不可只留下文字摘要。
- PNG 加入前先壓縮。Git 會保留每一版完整二進位內容，不能把圖片當文字差異看待。
- 實機畫面只用[允許的虛構圖書館名稱](../rules-code.md)，並檢查連線列、資料庫下拉與
  登入名稱。現有 `LibraryDB` 系列也是假資料；新圖優先沿用文件中的 `Lib_Reader` 系列。
- 每張內容圖都寫能獨立理解的 `alt`。不要把必要說明烙進圖片；那無法翻譯，也無法被
  螢幕閱讀器讀取。
- 畫面外框、主題與控制項必須來自目前支援的 SSMS 22；過時外框不再當正式截圖。

## 特殊檔案

`social-preview.png` 不放 README；到 GitHub 的 **Settings → General → Social preview**
上傳，供 Teams、Slack 與社群連結預覽使用。

`logo.png` 同時是 VSIX manifest 的 `<Icon>` 與 `<PreviewImage>`。專案以 `Link` 放入 VSIX
根目錄，殼層自行縮放，因此不要維護另一份縮圖。四角必須透明且沒有與黑底混色的暗邊，
才能同時適用 SSMS 深淺主題。

插畫來源與可重製提示詞見[生成提示詞](prompts.md)；`completion.png`、
`structure-preview.png` 與結果格線圖片則是實機畫面或標註後的實機畫面。
