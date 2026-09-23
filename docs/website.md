# 文件網站與操作動畫發布

本頁包含 VitePress 建置、Pages 部署與上線驗收；動畫重製與圖片用途見[圖片維護](images/README.md)。

## 來源與目錄

- 根目錄兩份 README 是網站中英文首頁的唯一內容來源。
- `docs/` 是功能文件；建置時將 Markdown 映射到 `/guide/`，原始碼連結改指 GitHub。
- `docs/images/` 放 GIF、PNG 與產品圖示；`docs/demos/` 放可暫停的 HTML 播放器與來源。
- `site/` 放 VitePress 設定、主題、建置腳本及套件鎖檔。`.build/` 與 `node_modules/` 不提交。
- `tools/` 保留動畫 SQL 產生、畫格擷取及 GIF 編碼入口；中間產物留在 `artifacts/`。

## 本機驗證

需要 Node.js 22，在專案根目錄執行：

```powershell
npm ci --prefix site
npm run build --prefix site
npm run preview --prefix site
```

用預覽服務顯示的網址加上 `/SqlAssist.Ssms22/` 開啟網站。確認中英文首頁、文件搜尋、
圖片、靜態圖與播放器；播放器預設停止，逐一驗證選擇場景、播放、暫停及重播。
GIF 是操作示意，不能取代 SSMS 實機功能驗收。

VitePress 維持穩定版；`package.json` 暫時覆寫 Vite 為已修補的 6.4.3，避免其原本 Vite 5
開發伺服器依賴的已知漏洞。升級時同步更新鎖檔，執行 `npm audit --prefix site`、正式建置
與預覽驗收；上游穩定版採用已修補依賴後移除此覆寫。

## 部署

GitHub Settings → Pages 的來源設為 **GitHub Actions**。PR 只建置；合併至 `master` 後
建置並部署，也可在 `master` 手動執行 Deploy GitHub Pages。建置不重製動畫，直接發布已驗收
且提交的 GIF、PNG 與播放器來源。缺少文件連結會使 VitePress 建置失敗。

部署後確認 `/SqlAssist.Ssms22/`、`/SqlAssist.Ssms22/en.html` 與
`/SqlAssist.Ssms22/demos/feature-demos.html` 可開啟，並檢查瀏覽器沒有資源 404 或腳本錯誤。
本機建置通過不代表 GitHub 環境權限與正式網址已驗證。

## 圖片保留原則

圖示的 `20`、`24`、`32`、`40`、`48`、`64`、`96`、`256`、`1024` 尺寸 PNG 未被程式或
網站引用，可視為候選；保留 SVG 母圖、512（VSIX）、128（網站）、16（選單來源）及 ICO。
`social-preview.png` 用於 GitHub 外部設定，不能只因沒有程式引用就刪除。
`*-demo.png` 是動畫的靜態替代；README 的操作圖片以展示頁產出的同名 GIF／PNG 為準。
