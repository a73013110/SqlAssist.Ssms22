# 圖片

`README.md` 與文件用到的圖片都放這裡。目前每一張都還沒有產出，README 裡對應的
`<img>` 已經先寫好並註解起來——檔案放進來之後把註解拿掉就會顯示。

## 檔案清單

| 檔名 | 用途 | 尺寸 | 怎麼來 |
|---|---|---|---|
| `hero.png` | README 標題下方的橫幅 | 1200 × 400 | 產生 |
| `social-preview.png` | GitHub 的 Social preview（分享連結時顯示的縮圖） | 1280 × 640 | 產生 |
| `logo.png` | 專案圖示，之後也可以當 VSIX 的圖示 | 512 × 512 | 產生 |
| `completion.png` | 建議清單 | 寬 880 | **實機截圖** |
| `expand-star.png` | `SELECT *` 展開前後 | 寬 880 | **實機截圖** |
| `structure-preview.png` | 浮動結構預覽 | 寬 880 | **實機截圖** |

`social-preview.png` 不會出現在 README 裡，要到 GitHub 的
**Settings → General → Social preview** 上傳。那張圖決定這個連結貼到 Teams、Slack
或社群時長什麼樣子，是「會不會有人點進來」影響最大的一張。

## 截圖不要用生成的

前三張是插畫，生成沒問題。後三張**一定要實機截圖**：這個專案的讀者天天在看 SSMS，
一張長得不太對的假 SSMS 視窗，只會讓人覺得整個專案也不太可靠。

截圖時：

- **只用虛構的圖書館領域命名**（`Lib_Reader`、`PUBLISHER`、`Cat_BookCopy`、`Loan`…）。
  這個 repo 是公開的，真實系統的資料表與欄位名稱本身就是使用者的私有資產，
  理由見 [CLAUDE.md](../../CLAUDE.md)。連線列與資料庫下拉選單也要一起避開。
- 六張圖統一用同一個 SSMS 佈景主題（深色或淺色擇一）與同一個縮放比例。
- 裁到剛好包住要講的東西，不要整個 SSMS 視窗——README 上縮圖之後什麼都看不清楚。
- 存成 PNG。

## 產生用的提示詞

提示詞刻意用英文寫：影像模型對英文的描述詞彙反應比較準，這裡的文字是丟給模型的
輸入而不是給人讀的說明。三張都**刻意要求不要出現可讀的文字**——影像模型拼出來的
英文單字幾乎都是壞的，而壞掉的字比沒有字更傷。

### `hero.png`（1200 × 400）

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

### `social-preview.png`（1280 × 640）

```text
A clean 1280x640 open-graph card for a developer tool. Deep indigo to charcoal gradient
background. Centred composition: a simplified database cylinder outlined in white on the
left, connected by a thin cyan line to a floating autocomplete list card on the right
with four rows of abstract highlighted bars. Wide empty margins at the top and bottom
for text to be added later. Flat vector, minimal, high contrast, no text, no letters,
no logos.
```

上傳前自己在圖上加「SqlAssist for SSMS 22」一行字——留白就是為了這個。
讓模型直接畫字幾乎一定會拼錯。

### `logo.png`（512 × 512）

```text
A minimal flat app icon, 512x512, centred on a rounded square with a deep indigo to blue
gradient. The mark is a simple database cylinder outlined in white with even thick
strokes, and a small bright cyan text-cursor bar standing beside it. Nothing else. Flat
vector, no text, no shadows, generous padding, still legible when scaled down to 32
pixels.
```
