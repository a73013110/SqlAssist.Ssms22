# 內建名稱的說明與辨識

本頁包含說明內容的出處、開在哪個表面，以及游標下那個名稱算哪一種內建名稱。
使用者自己的物件不走這條路，見[結構預覽](structure-preview.md)。

內建名稱有八種：函式、型別、資料表提示、查詢提示、datepart、全域變數、系統程序、語句。
兩個表面，分工與物件結構完全一樣。滑鼠停留提示給一眼看得完的份量：簽章、一行用途與
**第一段**範例。看不完的——`CONVERT` 的十六個 style、`sp_executesql` 的參數與五種寫法、
其餘幾段範例——交給浮動視窗，那裡捲得動也選得起來。

三個表面問的是同一個目錄、畫的是同一個建構器。這條路**不查資料庫**，沒有連線也答得出來。

## 完整說明開在哪裡

提示最後一行的「開啟完整說明」、名稱上按 **Ctrl+F12**、Ctrl＋點擊、建議清單選到內建項按
**向右鍵**，開的都是同一個浮動視窗，錨在同一個名稱上（見[預覽互動](preview-interaction.md)）。
「這一次要畫什麼」只有一個型別（`Ssms22/Preview/SqlPreviewSubject`）。

**裝不滿一個視窗就不開**：對照表或範例其中之一（`SqlBuiltInDoc.DeservesWindow`）。
`smallint` 只有一行說明，蓋上去等於把說明面板遮掉；那時向右鍵照常右移游標。
提示裡的「開啟完整說明」更嚴（`HasExpandedContent`）：有對照表或範例超過一段——
只有一段的話，提示已經印完了。

| 分頁 | 內容 |
|---|---|
| 範例 | 全部範例接成一份，每段前一行 `-- ▸ 標題`、段間 `GO` 加一行空白（`SqlBuiltInExampleText`）；整頁複製就能執行，各段宣告同名變數也不衝突 |
| 對照表 | 一張表一個分頁；系統程序依序是「參數」「寫法」（有傳回碼另加一張），語句第一張是「寫法」；開啟時選第一張 |

「複製全部」複製的是目前這張對照表。物件那七個分頁全部收起來，換回資料表時再換回去。
對照表分頁**重複使用**而不是每次重建：資料格連帶的內容選單會登記在視窗上跟著它一輩子。

## 資料分成幾處，不是一處

| 這件事 | 唯一出處 |
|---|---|
| 函式的簽章 | `Core/Keywords/SqlFunctionCatalog` |
| 型別的一行說明 | `Core/Keywords/SqlDataTypeCatalog` |
| 提示與 datepart 的一行說明 | `Core/Keywords/SqlArgumentCatalog` |
| 全域變數的一行說明 | `Core/Keywords/SqlGlobalVariableCatalog` |
| 系統程序與語句的簽章 | 資源那一筆的 `signature`（沒有另一份目錄） |
| 用途、範例、文件位址、對照表 | `Core/Keywords/BuiltInDocs/` |

資源依種類拆成 functions、types-hints、statements、system-procedures 與共用表格 tables，
各帶一份 `<名稱>.<語言>.json` 譯文（見[在地化](localization.md#資料型文字)），由
`SqlBuiltInDocCatalog` 合併。其他種類寫了 `signature` 就是第二份簽章，格式測試擋下；
同一名稱＋種類重複、別名（`aliases`，`EXECUTE` 共用 `EXEC` 那一份）互撞也一樣。

種類要與名稱對得上才算數：`YEAR` 在函式與 datepart 各有一筆，寫錯是把函式的範例貼到
datepart 上。提示與全域變數只寫範例與文件位址，一行說明照樣向目錄要。

範例是 `examples` 陣列（id、title、sql）。**第一段是提示用的**：單行、排得下提示的截斷上限；
第二段起只出現在視窗裡，只限行數。每段都要能直接執行、不依賴使用者的資料表，自己建的物件在同一段清掉——
`GO` 只切批次，暫存表與實體物件會留到下一段。

對照表有兩種寫法：幾筆共用的寫在 tables，由編號引用（`CONVERT` 與 `TRY_CONVERT` 指向
同一組 style，抄進每一筆就是改了一份其他還舊）；只屬於一筆的（系統程序的參數與寫法）
直接內嵌。`datePart` 由 `SqlArgumentCatalog` 組出來，不寫在資源裡。表格是沒有語意的欄
與列、上限四欄——一種形狀一個型別的話，每補一張表都要動 C# 與 WPF 兩層。

**與建議清單分家是為了成本**：建立候選清單只需要名稱與簽章，說明放在內嵌資源裡，
第一次查詢才解析。名稱涵蓋範圍比清單**廣**：`CONVERT`、`LEFT` 在清單裡讓給關鍵字，
提示沒有那個兩難。194 個函式都有說明，`SqlBuiltInDocCatalogTests` 逐一比對
`SqlFunctionCatalog.Names`，少一筆就是建置失敗。

開關是「滑鼠停留時顯示內建名稱的說明」，與物件提示分成兩個。資源讀不到降級成空字典、
**不**丟例外：這條路掛在滑鼠移動上，`Lazy` 還會把例外永久快取。線上文件只接受絕對的
https 位址；改過 `docsUrl` 後跑 `tools/Check-DocLinks.ps1`（要網路，不進離線檢查）。

## 辨識：游標下的名稱算哪一種

判斷整段在 `SqlBuiltInDocCatalog.TryGetAt`，不在平台接線層。

- **系統程序**靠名稱與限定字：目錄裡有這個名字，限定字是空的、`sys`、`master.sys` 或
  `master..`，位置不問（`EXEC` 後、`INSERT … EXEC` 後、批次第一句）。`dbo.sp_x` 是使用者的。
- **語句**只在語句開頭才算（`INSERT #t EXEC` 的 `EXEC` 也是），判準與位置分析同一支
  （`SqlStatementBoundaries.IsStatementHead`）；
  `BULK INSERT` 比照多字提示由長到短試，後面接 `AS` 的（`EXECUTE AS`）不算。
- **全域變數**靠 `@@`，位置不問。**提示與 datepart** 只在 `WITH (…)`、`OPTION (…)`、
  `DATEADD(` 第一個引數裡才算，問的是 `Core/Completion/SqlArgumentPosition`；這一關排在
  左括號之前，否則 `WITH (INDEX(1))` 的 `INDEX` 會被當成函式呼叫。
- **函式與型別**靠左括號，與自動大寫同一條規則（見[函式與型別](completion-builtins.md)）：
  `SELECT year FROM dbo.Loan` 的 `year` 是資料行。有括號先當函式，查不到才退到型別；反過來
  不成立。`CAST(x AS char(10))` 的 `char` 仍會被說成函式，那是位置分析的工作。
  `[CONVERT]`、`dbo.CONVERT` 都不是內建名稱。

多字提示只認第一個詞、由長到短試：`OPTIMIZE FOR` 與 `OPTIMIZE FOR UNKNOWN` 說的是相反的事。

## 說明與物件誰先

**系統程序與語句的說明排在物件解析之前**（`SqlBuiltInKinds.PrecedesObjectResolution`）：
SQL Server 解析 `sp_` 名稱本來就先找系統那一份，而且說明不必連線。**函式與型別排在之後**：
`SELECT * FROM Format` 停在 `Format` 上要的是那張表，物件解析對內建名稱一定落空，反過來會把
同名的資料表蓋掉。

順序只有一份（`Ssms22/Editor/SqlBuiltInObjectResolution`），Ctrl+F12、Ctrl＋點擊與滑鼠停留
共用；建議清單上帶系統結構描述的預存程序項也對到系統程序說明。沒寫說明的系統物件改走物件
那條路，見[物件種類](completion-object-kinds.md#系統物件只在三個位置拉進來)。
