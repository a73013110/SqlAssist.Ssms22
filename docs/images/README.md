# 圖片

`README.md` 與文件用到的圖片都放這裡。

## 檔案清單

| 檔名 | 用途 | 實際尺寸 | 狀態 |
|---|---|---|---|
| `hero.png` | README 標題下方的橫幅 | 2172 × 724 | 已接上 |
| `expand-star.png` | `SELECT *` 展開前後 | 1470 × 1070 | 已接上 |
| `completion.png` | 建議清單與欄位面板 | 1438 × 1093 | 已接上（收在 `<details>` 裡） |
| `structure-preview.png` | 浮動結構預覽的指令碼分頁 | 1494 × 1053 | 已接上（收在 `<details>` 裡） |
| `social-preview.png` | GitHub 的 Social preview | 1774 × 887 | 待上傳，見下 |
| `logo.png` | 專案圖示 | 1254 × 1254 | 已重畫，還沒接上任何地方，見下 |

README 裡的四張圖都指定 `width`，不是原尺寸貼上去：`hero.png` 給 900、其餘給 820。
原尺寸會把版面撐開，而 GitHub 不會幫忙縮。

每一張都壓過（`logo.png`、`hero.png` 等已轉成調色盤 PNG），維持原尺寸但檔案小得多。
補新圖時記得一起壓：這些圖每一次 clone 都會整份帶下來，而且圖片改一次就在 git 裡
多存一份完整的內容，不像文字檔只存差異。

### `social-preview.png` 不出現在 README

它要到 GitHub 的 **Settings → General → Social preview** 上傳。那張圖決定這個連結
貼到 Teams、Slack 或社群時長什麼樣子——README 的第一畫面只有已經點進來的人看得到，
這一張決定的是有沒有人點進來。

### `logo.png` 目前還沒接到任何地方

這一張已經重畫過，圖示的三個基本條件都補上了：正方形（1254 × 1254）、圓角方形的
深靛藍到藍色漸層底、銳利的白色圓柱與亮青色游標，四周留白、沒有任何一邊被畫布切掉，
縮到 32 點仍然認得出是「資料庫＋游標」。

還沒接上是因為現在沒有需要它的位置，不是因為它不能用：

- **README 用不到**。標題下方已經有 `hero.png`，同一個畫面再放一顆圖示只是重複。
- **VSIX 還沒有圖示資產**。`source.extension.vsixmanifest` 目前沒有 `Icon` 與
  `PreviewImage`，要接的話得同時加資產宣告與專案檔的 `Content` 項目，那是建置的
  改動不是文件的改動。

圓角外的四個角落已經裁成透明（半徑 239 的圓角，四邊各內縮 2 點），淺色底上不會看到
黑角，也沒有沿著圓弧的暗邊——那 2 點就是為了把原本與黑底混色的那一圈排掉，留在裡面
會變成一條灰邊。裁切後仍然是調色盤 PNG，164 個項目、17 級 alpha，不透明區的每一個
像素與裁切前逐位元相同。

## 這些圖是怎麼來的

`hero.png`、`social-preview.png` 是生成的插畫；`completion.png`、
`structure-preview.png` 是實機畫面；`expand-star.png` 是加了「展開前／展開後」
說明框的合成圖。

合成圖要留意兩件事，決定要不要重做時可以參考：

- 圖上那個視窗外框不是 SSMS 22 的樣子（SSMS 22 是 VS 2022 的外觀）。天天在看 SSMS
  的讀者會發現對不起來。
- 說明框上的字是圖片的一部分，改文案就得重畫，翻譯與螢幕閱讀器也讀不到。
  README 裡每一張都寫了完整的 `alt`，至少讀得到的那一份是有的。

畫面圖一律**只用虛構的圖書館領域命名**。這個 repo 是公開的，真實系統的資料表與欄位
名稱本身就是使用者的私有資產，理由見 [CLAUDE.md](../../CLAUDE.md)。連線列、資料庫
下拉選單與登入名稱也要一起看過——那三個地方最容易漏。

目前這幾張用的是 `LibraryDB`／`Libraries`／`LibraryAnnouncement` 這一套，
與文件和測試裡的 `Lib_Reader`、`PUBLISHER`、`Cat_BookCopy`、`Loan` 不是同一套寫法。
兩者都在同一個虛構領域裡，沒有外洩問題；只是下次補圖時沿用文件那一套會更一致。

## 產生用的提示詞

提示詞刻意用英文寫：影像模型對英文的描述詞彙反應比較準，這裡的文字是丟給模型的
輸入而不是給人讀的說明。插畫都**刻意要求不要出現可讀的文字**——影像模型拼出來的
英文單字幾乎都是壞的，而壞掉的字比沒有字更傷。

### `hero.png`

```text
A wide modern developer-tool hero banner, 1200x400. Dark charcoal background with a
subtle diagonal gradient toward deep indigo. On the left, an abstract stylized code
editor panel: soft-focus rows of monospaced code in muted grey-blue, deliberately
unreadable. On the right, a crisp floating autocomplete card with rounded corners, a
thin light border and five list rows; each row has a small coloured glyph on the left
and a horizontal bar standing in for a name, with the first few characters of each bar
highlighted in bright cyan to suggest matched letters. A soft cyan glow links the
editor to the card. Flat vector illustration, clean, high contrast, generous negative
space. No logos, no people, no legible words or letters anywhere.
```

### `social-preview.png`

```text
A clean 1280x640 open-graph card for a developer tool. Deep indigo to charcoal gradient
background. Centred composition: a simplified database cylinder outlined in white on the
left, connected by a thin cyan line to a floating autocomplete list card on the right
with four rows of abstract highlighted bars. Wide empty margins at the top and bottom
for text to be added later. Flat vector, minimal, high contrast, no text, no letters,
no logos.
```

### `logo.png`（現在這一張就是照這份重畫的）

```text
A minimal flat app icon. The canvas must be exactly square, 512x512. The artwork is a
rounded square tile with a deep indigo to blue gradient, with clear empty padding
between the tile edge and the mark. Centred on the tile: a simple database cylinder
outlined in white with even, sharp, uniform strokes, and a small bright cyan
text-cursor bar standing beside it. Crisp vector edges with no glow, no blur, no drop
shadow and no bloom. Nothing is cropped by the canvas edge. Flat vector, no text, no
letters, still clearly legible when scaled down to 32 pixels.
```
