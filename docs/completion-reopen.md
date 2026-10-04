# 建議清單重開

本頁包含詞元結束後何時重開清單、重開的三個步驟；範圍怎麼切與列出哪些欄位見[範圍](completion-scope.md)，
提交進去的文字見[插入文字](completion-insertion.md)。

## 詞元一結束就把清單重開

平台的規則是「沒有 session 就問建議來源要不要開，已經有 session 就只重新篩選」。
這對識別字是對的：多打一個字母只是把候選變少。但**結束詞元的字元**不是——
它會讓上下文整個換掉，而還開著的那份清單是照舊上下文組出來的：

```text
SELECT a       → 清單開著，裡面是關鍵字與資料庫物件
SELECT a.      → 平台拿 a. 去比對同一份清單，一個都比不中，清單默默關掉
SELECT a.N     → 這時才重新問來源，欄位清單終於出現
```

因此輸入這類字元時自己把 session 收掉再開一次，但只在舊清單還開著或字元被自己吞掉時——
其餘情形平台自己會開，再重開就是「開、關、開」（`FROM [` 的自動配對先收掉了清單）。
這一道在按鍵當下問（`SqlCompletionReopen.AfterTypedCharacter`），上下文換不換等字元
進了緩衝區再由 `SqlCompletionTriggers` 判斷：

- 前一個字元還能構成識別字（`SELECT CUST|`）→ 不重開，平台自己的篩選是對的。
- **小老鼠除外**：它構得成識別字（`@@ROW` 的詞元起點必須落在第一個小老鼠上），
  但打出來的那一刻目標會整個換掉。`INSERT INTO ` 開著的是資料表清單，
  `INSERT INTO @` 要的是使用者自己宣告的變數，兩份沒有一項重疊——症狀是
  「單打一個 `@` 什麼都沒有，`@S` 才有提示」。`@@` 同理，那又是另一份封閉清單。
  判斷與呼叫端共用 `SqlCompletionTriggers.MayChangeContext`：一邊放行、
  另一邊擋掉，等於沒改。
- **候選集合封閉**（`SqlCompletionPolicy.IsClosed`：下一個詞元一定是清單上的某一項）→ 重開。
  線索有四種：限定字（`a.`、`[dbo].`）與左方括號；目標收斂（`FROM `、`EXEC `、`DATEADD(`、
  封閉片語；可能是名字的那一格不算，它寫得出清單外的新名字）；文法指定了資料行的所屬資料表（`UPDATE t SET `，見[欄位](completion-columns.md#文法指定的所屬資料表)）；
  只接那幾個字的關鍵字位置（`ORDER `、MERGE 的 `THEN `，`SqlKeywordPositionExtensions.IsClosed`）。
  少了任何一種，那個位置就要多打一個字母才有清單——與點號完全同一個病。
- 其餘（`SELECT `、`COUNT(`、`ORDER BY `、`WHERE a `）→ 不重開：接得了運算式、常值、
  逗號或下一句，按一下空白鍵就開的話是整個資料庫。

這幾條與建議來源是同一條參與規則（`SqlCompletionPolicy.Participates`），不另寫一份：
最後一條是空前綴沒到觸發字元數，`12.` 的點號是數值常值（`Inert`），名字那一格不開。
自己開出來的清單還沒打字，一律軟選，見[補全](completion.md#清單內容與排名)。

## 重開清單的三個步驟

片段接續與分隔字元走的是同一段程式（`SqlCompletionReopen`），三個步驟一個都不能少：

```csharp
broker.GetSession(view)?.Dismiss();                  // 1
var session = broker.TriggerCompletion(view, trigger, caret, token);   // 2
session?.OpenOrUpdate(trigger, caret, token);        // 3
```

1. **先收掉舊的。** `TriggerCompletion` 一開頭就先問 `GetSession`，只要還有 session
   就原封不動把它交回來——不先收掉，整個呼叫沒有任何作用。`Dismiss` 是同步的，
   回傳之前就已經把自己從 broker 的紀錄裡拿掉。
2. **`TriggerCompletion` 只是建立 session**：問過各個來源要不要參與、算出適用範圍，
   然後就結束了。
3. **`OpenOrUpdate` 才會去要清單並把 UI 畫出來。** 少了這一行，前面每一步都算對了，
   畫面上仍然什麼都不會出現。平台自己的命令處理常式在同一個位置也是這樣接著寫的。

整段排在派送佇列的 Background 優先權上執行，不在原地直接呼叫：提交當下平台正要
把 session 收掉，而輸入字元當下那個字元還沒進緩衝區——在原地開出來的清單，
看到的是上一個狀態。
