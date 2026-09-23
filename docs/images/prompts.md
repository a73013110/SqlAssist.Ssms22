# 靜態品牌圖生成提示詞

本頁只保存 `hero.png`、`social-preview.png` 的可重製模型輸入；
尺寸、壓縮、替代文字與發布規則見[圖片護欄](README.md)。提示詞保留英文以提高影像模型理解度；
文案不得生成在圖內，應留在 HTML／Markdown。

## `hero.png`

```text
Use case: ui-mockup
Asset type: README hero image for SqlAssist, a SQL Server Management Studio 22 extension.
Primary request: Create a polished, ultra-wide 3:1 developer-tool hero illustration
showing the real core interaction: a dark SQL editor with an autocomplete popup, and a
larger object-structure preview opening beneath it, both visually connected to the cursor.
Scene/backdrop: SSMS-like charcoal interface, understated window chrome, query editor
occupying the left and center. In the editor, a few restrained lines of syntax-colored
SQL-shaped marks. The selected suggestion resembles a database table row. The preview
presents a small table/schema grid and a short DDL code block. These should look like
plausible technical UI, not a generic chatbot or AI dashboard.
Style/medium: precise, high-end interface illustration, clean edges, subtle depth,
restrained highlights.
Composition/framing: panoramic 1200x400 proportion, all important panels within safe
margins, balanced visual weight across the width; legible hierarchy when displayed 900px wide.
Lighting/mood: focused, calm, professional; a subtle blue-cyan accent at the selected
completion row and cursor, muted violet outline accents.
Color palette: dark charcoal #252529, slate #34343a, blue-cyan #69bfe8, restrained
violet #9489cc, white-grey text bars.
Constraints: no actual readable text or letters anywhere, no fake labels, no people,
no logos, no extra windows or unrelated UI. Show completion and schema preview clearly.
A continuous full-bleed banner, not a graphic floating in empty space.
```

## `social-preview.png`

```text
A clean 1280x640 open-graph card for a developer tool. Deep indigo to charcoal gradient
background. Centred composition: a simplified database cylinder outlined in white on the
left, connected by a thin cyan line to a floating autocomplete list card on the right
with four rows of abstract highlighted bars. Wide empty margins at the top and bottom
for text to be added later. Flat vector, minimal, high contrast, no text, no letters,
no logos.
```
