# 篩選列與過濾面板

工具列上那一列篩選、它的分隔線、過濾面板與展開箭頭；SQL Memory 與 SQL Search 共用同一份。
這一層只放**縮小搜尋範圍**的條件；修飾字串怎麼比的直接控制與作用在這一份結果的操作都在
第一列，界線見[視窗骨架](ui-windows.md#第一列輸入列)。
骨架與狀態表面見[視窗骨架](ui-windows.md)，視覺語言見 [UI 準則](ui-guidelines.md)，
元件的唯一出處見[平台共用元件](shared-components-platform.md)，驗收項目在[視窗骨架](ui-windows.md#驗收)那一份裡。

## 分隔線與換行

- **分隔線分兩級**（`CreateFilterGroupDivider`／`CreateFilterItemDivider`），每一顆之間都有線：
  群間高 18、左右各 6，群內矮一截淡一階（高 10、左右各 4、0.55）。分群規則一條：回答**同一個
  問題**的是一群——Memory 是「狀態｜期間｜連線」，Search 是「搜哪裡｜搜什麼｜比對哪裡」。兩級
  畫成同一種等於取消分群，使用者會把「種類」讀成第三個範圍。線不表達狀態，用 `Hairline`。
- **那一列篩選**（`SqlFilterBar`）唯一一份：先收字（只有 `SqlFilterFlyout` 降得了）再**依群**
  換行，交給換行面板會讓三顆裡的一顆單獨掉到下一列；列首與整群 `Collapsed` 的（Favorites 的
  狀態與期間）都收起自己前面那一條，否則列首從一條孤線開始。

## 過濾面板

- Filter Flyout（`SqlFilterFlyout`）是**唯一一份**過濾面板：Search 的伺服器／資料庫／種類與
  Memory 的伺服器／資料庫都是它。支援 `Single`／`Multiple`／`SearchableMultiple`；單選用 radio、
  選完自動關閉；分類保留 provider group 與 sort order。「沒有勾任何一個」是一個**實際的預設**，
  所以它是面板第一列（`SetEmptyOption`）而不是一片空白——摘要寫著「全部」而一個勾都沒有時，
  使用者會以為條件弄丟了。那一列勾得上去、取消不掉，字與按鈕摘要共用一份且**由宿主給**：
  Search 的未選是「連線預設」，Memory 的未選是「全部」，控制項不替它們挑一句。命令鈕只剩
  **全選**，且只有可搜尋的那一種有；「清除」與第一列是同一件事，不畫第二個入口。別的選項變動時
  只改那一列（`SyncEmptyOption`）：重建整份會把捲動位置與鍵盤焦點一起丟掉，而使用者可能正在
  連勾好幾個。續頁（`SetMore`）與排序（`SetSortOptions`）是**面板等級的一般能力**，不綁任何一個
  功能的值：兩顆鈕都在捲動區外面一直看得見，位移與頁大小留在宿主，換排序只問出去、宿主寫回來
  才換按鈕。選項清單是虛擬化 `ItemsControl`，不用 `ScrollViewer + StackPanel`；快取 Style／
  ControlTemplate，不快取有 parent 的 `UIElement`。
- **展開／收合箭頭**（`CreateChevron`／`SetChevronExpanded`）也只有一份：收合朝右、展開朝下，
  轉過去而不是跳過去，而且綁的是 `Popup` 的開／關——綁 Click 的那一版在面板被按到外面關掉
  之後箭頭仍朝下，指著一個不在畫面上的面板。

