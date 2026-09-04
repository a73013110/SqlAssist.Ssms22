# 片段與解析護欄

修改 Snippets、Parsing、Wildcards、補全上下文或 SQL 掃描前必讀。
- **禁止**在 Snippet 樣板裡把結構描述與物件名稱拆成 `$schema$.$object$` 兩格。
  第一格的答案幾乎永遠是 `dbo`，而建議清單依設定插進來的 `[dbo].[Lib_Reader]`
  這種寫法根本填不進拆開的格子。

- **禁止**為 Snippet 欄位另外宣告「這一格要列哪一類物件」。那份判斷在
  `SqlCompletionContextAnalyzer`，它讀的是實際文字；多一份宣告的症狀是樣板改了、
  宣告沒改，而清單靜靜地不再出現。

- **禁止**把 Snippet 欄位的上下文一律截到該格起點。只有「整格還是樣板填的預設值」
  那一次要當它不存在；使用者一打字，那幾個字就是前綴，而那是無限定字的格子
  （`INSERT (|)`）唯一的參與條件。截點只有
  `SqlSnippetExpansionController.ResolveAnalysisEnd` 一份，排名器也要照同一條
  把預設值視為空前綴，否則 Tab 進去的清單會被自己的預設值濾光。

- **禁止**再寫一份 SQL 註解略過或括號配對。`Core/Parsing` 的 `SqlTrivia` 與
  `SqlTokenNavigator` 是唯一出處；自己寫的那一份漏掉巢狀註解已經發生過一次。

- **禁止**做部分展開。`SELECT *` 只要有一個來源解析不出來就完全不展開；
  少幾個欄位的 `SELECT` 執行得動卻執行出錯的結果，比什麼都不做糟。
