using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

public static class SqlCompletionContextAnalyzer
{
    /// <summary>
    /// 分析游標前方的文字。
    /// </summary>
    /// <remarks>
    /// 只看游標之前的文字，因此無法解析別名：<c>SELECT u.| FROM Lib_Reader u</c>
    /// 的 FROM 子句在游標後方。需要欄位建議時請改用帶完整文字的多載。
    /// </remarks>
    public static SqlCompletionContext Analyze(string textBeforeCaret)
    {
        if (textBeforeCaret is null)
        {
            throw new ArgumentNullException(nameof(textBeforeCaret));
        }

        var tokenStart = FindTokenStart(textBeforeCaret);

        if (!SqlLexicalContext.IsCode(textBeforeCaret, tokenStart))
        {
            return new SqlCompletionContext(false, tokenStart, string.Empty, CompletionTarget.Any);
        }

        // 小老鼠開頭的詞元不必看位置，也不必看前導關鍵字：它要的東西只有兩種，
        // 而兩種都與周圍的文法無關。
        if (tokenStart < textBeforeCaret.Length && textBeforeCaret[tokenStart] == '@')
        {
            return AnalyzeVariable(textBeforeCaret, tokenStart);
        }

        // 數字開頭的詞元是一個數值常值：T-SQL 的一般識別字不能以數字開頭，
        // 所以清單裡沒有一項會是對的。位置分析在這裡也幫不上忙——運算子之後
        // 一律是 Any，於是 SET Quantity = Quantity - 10 打到 10 的時候整個目錄
        // 進場，模糊比對撈回 LOG10，而使用者順手按下 Enter 就把數字換成了
        // 一個函式名稱。擋數值常值與 SqlCompletionTriggers.IsIdentifierLike
        // 不讓 1.5 的點號彈出物件清單是同一條理由。
        if (tokenStart < textBeforeCaret.Length && char.IsDigit(textBeforeCaret[tokenStart]))
        {
            return new SqlCompletionContext(false, tokenStart, string.Empty, CompletionTarget.Any);
        }

        // 限定字之後（dbo.| 或 u.|）要的是名稱，關鍵字在那裡一個都不該出現，
        // 但這裡不用特別處理：限定字會讓 Target 收斂，關鍵字已經被目標過濾擋掉。
        //
        // 詞法分析只做一次：位置與「這裡是不是型別的位置」問的是同一段文字，
        // 各自再分析一次的話，每按一鍵就把游標前的整份指令碼掃兩遍。
        var textBeforeToken = textBeforeCaret.Substring(0, tokenStart);
        var tokens = SqlTokenizer.Tokenize(textBeforeToken);
        var keywordPosition = SqlKeywordPositionAnalyzer.Analyze(tokens, textBeforeToken);
        var prefix = textBeforeCaret.Substring(tokenStart);
        var beforeToken = textBeforeToken.TrimEnd();
        var qualifierPath = ExtractQualifierPath(beforeToken, out var beforeQualifier);

        // 引數與提示的封閉清單同樣排在「這裡不接受任何關鍵字」之前：
        // 那幾個位置除了清單上的字沒有別的東西是對的。
        if (SqlArgumentPosition.TryResolve(tokens, out var argumentTarget))
        {
            return new SqlCompletionContext(isValid: true, tokenStart, prefix, argumentTarget);
        }

        // 型別的位置要排在「這裡不接受任何關鍵字」之前問：CAST(x AS | 在位置分析
        // 眼中與 SELECT x AS | 的別名一模一樣，會被那一條整份收掉。
        //
        // 限定字要帶著走：DECLARE @t dbo.| 只該列出 dbo 的自訂型別，
        // 而內建型別沒有結構描述，會被結構描述過濾自己擋掉——dbo.INT 不是東西。
        if (SqlDataTypePosition.IsDataTypeSlot(tokens))
        {
            return new SqlCompletionContext(
                isValid: true,
                tokenStart,
                prefix,
                CompletionTarget.DataType,
                qualifierPath);
        }

        // 這個位置文法上只能是使用者自己取的名字：衍生資料表的別名、AS 之後的別名、
        // 變數與參數的名稱。清單裡沒有一項會是對的，而彈出來的唯一效果是使用者
        // 順手按下 Enter，剛打的 a 被換成 ALTER PROCEDURE——那是要按復原才救得回來
        // 的損失，而少一份清單只是少了幾個字母的補字。
        if (keywordPosition == SqlKeywordPosition.None)
        {
            return new SqlCompletionContext(false, tokenStart, string.Empty, CompletionTarget.Any);
        }

        // CREATE INDEX ix ON | 的 ON 後面是資料表，JOIN b ON | 的 ON 後面是述詞。
        // 這一條先問，因為它是唯一需要看詞元的：DetermineTarget 只認得游標前一、
        // 兩個詞元的字面值，而分辨這兩種 ON 要再往前看一個名稱單位。
        // 判斷本身與範圍分析共用 SqlDdlTarget——分岔的症狀是清單列得出資料表、
        // 欄位卻一個都沒有。
        var ddlOn = SqlDdlTarget.FindTrailingDataSourceOn(tokens);

        if (ddlOn >= 0)
        {
            return new SqlCompletionContext(
                isValid: true,
                tokenStart,
                prefix,
                CompletionTarget.DataSource,
                qualifierPath,
                tokens[ddlOn].Start,
                CompletionIntent.Reference,
                columnSources: null,
                keywordPosition);
        }

        var target = DetermineTarget(
            qualifierPath is null ? beforeToken : beforeQualifier,
            out var targetKeywordStart,
            out var intent);
        var isValid = prefix.Length > 0 || target != CompletionTarget.Any || qualifierPath is not null;

        return new SqlCompletionContext(
            isValid,
            tokenStart,
            prefix,
            target,
            qualifierPath,
            targetKeywordStart,
            intent,
            columnSources: null,
            keywordPosition);
    }

    /// <summary>
    /// 分析整份文字中游標所在的位置，補上敘述看得到的欄位來源，
    /// 並在限定字指向敘述內的資料來源時把建議目標改成欄位。
    /// </summary>
    /// <remarks>
    /// 必須看得到游標後方的文字：<c>SELECT u.| FROM dbo.Lib_Reader u</c> 這種
    /// 編輯既有查詢的情形，FROM 子句在游標之後，只看前文永遠解析不出 <c>u</c>。
    ///
    /// 一次詞法分析算完兩件事：呼叫端只要拿 <see cref="SqlCompletionContext.ScopeSources"/>，
    /// 不必再掃一次同一份文字——這條路徑在每一次按鍵上。
    /// </remarks>
    public static SqlCompletionContext Analyze(string sql, int caretPosition)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (caretPosition < 0 || caretPosition > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(caretPosition));
        }

        var context = Analyze(sql.Substring(0, caretPosition));

        // 游標在字串或註解裡，這一輪什麼都不建議，敘述有哪些資料來源也就無關。
        if (!context.IsValid)
        {
            return context;
        }

        // 全域變數與敘述看得到哪些欄位無關，底下整趟範圍解析可以省下來。
        if (context.Target == CompletionTarget.GlobalVariable)
        {
            return context;
        }

        // 變數只需要「這份指令碼裡出現過哪些 @名稱」，同樣不必解析範圍與欄位來源。
        // 資料表變數要多帶一份資料行清單：INSERT INTO @rows 提交之後展的是整句，
        // 而那份清單只存在於 DECLARE @rows TABLE (…) 裡。
        if (context.Target == CompletionTarget.Variable)
        {
            var variableTokens = SqlTokenizer.Tokenize(sql);

            return context.WithScriptSources(SqlScriptVariableSuggestions.Create(
                variableTokens,
                caretPosition,
                SqlScriptTableCollector.Collect(variableTokens)));
        }

        var tokens = SqlTokenizer.Tokenize(sql);
        var scope = SqlScopeAnalyzer.Analyze(tokens, caretPosition);
        var resolver = new SqlColumnSourceResolver(tokens);
        var withScope = context.WithScopeSources(resolver.ResolveAvailable(scope.Tables));

        if (context.QualifierPath is null)
        {
            // CTE 與暫存資料表只存在於這份指令碼裡，中繼資料查不到它們。
            // 只在真的要列資料來源時才掃：這條路徑在每一次按鍵上，
            // 而 FROM、JOIN 之後才是唯一用得到這一份的位置。
            return context.Target == CompletionTarget.DataSource
                ? withScope.WithScriptSources(SqlScriptDataSourceSuggestions.Create(
                    tokens,
                    resolver.CommonTableExpressionNames,
                    resolver.ScriptTables))
                : withScope;
        }

        // 前方關鍵字已經指定了物件類別（FROM、JOIN、EXEC…），代表游標正在輸入
        // 資料來源本身，此時點號前面必然是結構描述而不是別名：
        // FROM dbo.| 要列出 dbo 的物件，FROM u.| 這種寫法並不存在。
        if (context.Target != CompletionTarget.Any)
        {
            return withScope;
        }

        // 多段的限定字不可能是別名：別名只有一段，而 LibArchive.dbo. 這種寫法
        // 說的是「哪一個資料庫的哪一個結構描述」。拿最右邊那一段去比對別名的話，
        // 剛好取名叫 dbo 的別名會讓清單改列它的欄位。
        if (!context.QualifierPath.IsLocal ||
            context.Qualifier is null ||
            !scope.TryResolve(context.Qualifier, out var table))
        {
            return withScope;
        }

        // 資料表變數的欄位既不在指令碼裡也不在中繼資料裡，只能維持原本的
        // 結構描述解讀，讓使用者至少還看得到物件清單。
        var columns = resolver.Resolve(table);

        return columns is null ? withScope : withScope.AsColumnsOf(columns);
    }

    /// <summary>
    /// 游標停在一個小老鼠開頭的詞元上。
    /// </summary>
    /// <remarks>
    /// 兩個小老鼠開頭的是系統的全域變數：那是一份封閉的清單，使用者打出
    /// <c>@@</c> 的當下就已經說完他要什麼了。
    ///
    /// 一個小老鼠開頭的是變數或參數，那要分兩種：他正在<b>宣告</b>一個新名字時
    /// 清單裡沒有一項會是對的，而彈出來的唯一效果是他順手按下 Enter，剛打的
    /// <c>@pub</c> 被換掉——那要按復原才救得回來；他正在<b>引用</b>時要的正是
    /// 上面幾行宣告過的名稱，與 CTE、暫存資料表完全同格。
    /// </remarks>
    private static SqlCompletionContext AnalyzeVariable(string textBeforeCaret, int tokenStart)
    {
        var prefix = textBeforeCaret.Substring(tokenStart);

        if (prefix.Length >= 2 && prefix[1] == '@')
        {
            return new SqlCompletionContext(
                isValid: true,
                tokenStart,
                prefix,
                CompletionTarget.GlobalVariable);
        }

        // 只吃詞元之前那一段：正在打的名字本身當然不算數，而這一段的詞法分析
        // 與一般位置的 SqlKeywordPositionAnalyzer 是同一個代價。
        var tokens = SqlTokenizer.Tokenize(textBeforeCaret.Substring(0, tokenStart));

        if (SqlScriptVariableSuggestions.IsDeclarationSlot(tokens, tokens.Count))
        {
            return new SqlCompletionContext(false, tokenStart, string.Empty, CompletionTarget.Any);
        }

        // INSERT INTO @rows 與 MERGE INTO @rows 提交之後要展開的是整句，與
        // INSERT INTO dbo.Loan 完全同格——差別只在清單裡放的是他自己宣告的名稱。
        // 少了這兩行的症狀是：資料表變數選得到，卻只補了一個名稱，
        // 每一個欄位仍然要自己打一遍。
        //
        // 只收資料來源位置：EXEC dbo.p @ 的 @ 後面是引數而不是那句話的目標，
        // 在那裡帶著 ExecuteCall 會讓提交去展開一個變數。
        var beforeToken = textBeforeCaret.Substring(0, tokenStart).TrimEnd();
        var statementTarget = DetermineTarget(beforeToken, out var keywordStart, out var intent);

        if (statementTarget != CompletionTarget.DataSource)
        {
            keywordStart = -1;
            intent = CompletionIntent.Reference;
        }

        // EXEC dbo.usp_Renew @| 的位置除了他自己的變數，還要列出那個程序的參數。
        // 參數在中繼資料裡，這裡只記下他在呼叫誰。
        return new SqlCompletionContext(
            isValid: true,
            tokenStart,
            prefix,
            CompletionTarget.Variable,
            targetKeywordStart: keywordStart,
            intent: intent,
            executedModule: SqlExecutedModule.Find(tokens));
    }

    /// <summary>
    /// 依游標前方的關鍵字判斷應該建議哪一類物件，並回報該關鍵字的起點。
    /// </summary>
    private static CompletionTarget DetermineTarget(
        string text,
        out int keywordStart,
        out CompletionIntent intent)
    {
        // IF EXISTS 是 DROP 家族共用的修飾字，先剝一次就不必為 DROP TABLE、
        // DROP TRIGGER、DROP SEQUENCE 各寫一條加長版比對。只砍尾端，前面每個詞元的
        // 位置都沒有位移，因此底下算出來的 keywordStart 仍然指得回原文。
        text = TrimTrailingIfExists(text);

        // ALTER 之後要放進完整定義，因此與 EXEC 之類的單純參考分開表示。
        intent = CompletionIntent.AlterDefinition;

        if (EndsWithKeywords(text, "ALTER", "PROCEDURE", out keywordStart) ||
            EndsWithKeywords(text, "ALTER", "PROC", out keywordStart))
        {
            return CompletionTarget.Procedure;
        }

        if (EndsWithKeywords(text, "ALTER", "FUNCTION", out keywordStart))
        {
            return CompletionTarget.Function;
        }

        // 檢視與觸發程序在 SqlObjectKinds.IsModule 裡與程序、函式同一類，
        // OBJECT_DEFINITION 一樣拿得到定義，因此 ALTER 之後同樣放進完整定義。
        // 少了檢視這一條的症狀不是「清單怪怪的」而是 ALTER VIEW 之後整份清單
        // 都是資料表與關鍵字，選中的名稱在那個語句裡一定失敗。
        if (EndsWithKeywords(text, "ALTER", "VIEW", out keywordStart))
        {
            return CompletionTarget.View;
        }

        if (EndsWithKeywords(text, "ALTER", "TRIGGER", out keywordStart))
        {
            return CompletionTarget.Trigger;
        }

        // INSERT INTO 之後選一張資料表，要的幾乎不會是「只把名稱補上」——那句話還沒寫完。
        // 光看 INTO 分不出來：SELECT … INTO #tmp 的 INTO 後面是一個還不存在的新名稱，
        // 展開成 INSERT 骨架會蓋掉他正在取的名字。所以認的是 INSERT INTO 這兩個字。
        intent = CompletionIntent.InsertStatement;

        if (EndsWithKeywords(text, "INSERT", "INTO", out keywordStart))
        {
            return CompletionTarget.DataSource;
        }

        // MERGE 與 INSERT 同一條理由，而且更成立：那句話還沒寫完，
        // 而 MERGE 是三個子句都要逐欄重打的語句。INTO 可以省略（MERGE dbo.T AS t），
        // 兩種寫法都要認——漏掉哪一種都是那個寫法安靜地退化成只補名稱。
        // 這一條必須排在下面單獨的 INTO 之前，否則 MERGE INTO 會被那一條接走。
        intent = CompletionIntent.MergeStatement;

        if (EndsWithKeywords(text, "MERGE", "INTO", out keywordStart) ||
            EndsWithKeyword(text, "MERGE", out keywordStart))
        {
            return CompletionTarget.DataSource;
        }

        intent = CompletionIntent.ExecuteCall;

        if (EndsWithKeyword(text, "EXEC", out keywordStart) ||
            EndsWithKeyword(text, "EXECUTE", out keywordStart))
        {
            return CompletionTarget.Procedure;
        }

        intent = CompletionIntent.Reference;

        if (EndsWithKeywords(text, "DROP", "TRIGGER", out keywordStart) ||
            EndsWithKeywords(text, "DISABLE", "TRIGGER", out keywordStart) ||
            EndsWithKeywords(text, "ENABLE", "TRIGGER", out keywordStart))
        {
            return CompletionTarget.Trigger;
        }

        // DROP 之後要的只是一個名稱，因此與同名的 ALTER 分在不同的意圖。
        // 模組家族每一種都要各寫一條：漏掉的那一種沒有任何徵兆，只是使用者在
        // 那個位置沒有清單，而那正是 ALTER VIEW 之前的處境。
        if (EndsWithKeywords(text, "DROP", "VIEW", out keywordStart))
        {
            return CompletionTarget.View;
        }

        if (EndsWithKeywords(text, "DROP", "PROCEDURE", out keywordStart) ||
            EndsWithKeywords(text, "DROP", "PROC", out keywordStart))
        {
            return CompletionTarget.Procedure;
        }

        if (EndsWithKeywords(text, "DROP", "FUNCTION", out keywordStart))
        {
            return CompletionTarget.Function;
        }

        // 這三個位置文法上只接得了既有的資料表。ALTER 家族的 PROCEDURE／FUNCTION／
        // TRIGGER 與 DROP 家族的 TRIGGER／SEQUENCE 都已經在這裡，只差資料表——
        // 少的那一條沒有任何症狀，只是使用者在最常改的位置沒有清單。
        if (EndsWithKeywords(text, "ALTER", "TABLE", out keywordStart) ||
            EndsWithKeywords(text, "DROP", "TABLE", out keywordStart) ||
            EndsWithKeywords(text, "TRUNCATE", "TABLE", out keywordStart))
        {
            return CompletionTarget.DataSource;
        }

        // NEXT VALUE FOR 的尾巴就是 VALUE FOR；再往前的 NEXT 不必看。
        if (EndsWithKeywords(text, "VALUE", "FOR", out keywordStart) ||
            EndsWithKeywords(text, "ALTER", "SEQUENCE", out keywordStart) ||
            EndsWithKeywords(text, "DROP", "SEQUENCE", out keywordStart))
        {
            return CompletionTarget.Sequence;
        }

        if (EndsWithKeyword(text, "USE", out keywordStart))
        {
            return CompletionTarget.Database;
        }

        // CROSS／OUTER APPLY 之後文法上只接得了資料表值函式與衍生資料表，資料表
        // 本身放在那裡雖然剖析得過卻沒有意義。認的是 APPLY 一個字：前面那個
        // CROSS／OUTER 不改變後面要什麼，多比一次只是多一條會漏的路。
        if (EndsWithKeyword(text, "APPLY", out keywordStart))
        {
            return CompletionTarget.TableFunction;
        }

        // USING 與 FROM 是同一條文法（MERGE 的來源）。SqlKeywordPositionAnalyzer 與
        // SqlScopeAnalyzer 早就這樣歸類，只有這一份漏掉——症狀是 USING 之後完全沒有
        // 清單，而使用者看不出它和 FROM 之後有什麼不同。
        if (EndsWithKeyword(text, "FROM", out keywordStart) ||
            EndsWithKeyword(text, "JOIN", out keywordStart) ||
            EndsWithKeyword(text, "UPDATE", out keywordStart) ||
            EndsWithKeyword(text, "INTO", out keywordStart) ||
            EndsWithKeyword(text, "USING", out keywordStart))
        {
            return CompletionTarget.DataSource;
        }

        keywordStart = -1;
        return CompletionTarget.Any;
    }

    /// <summary>剝掉尾端的 <c>IF EXISTS</c>；沒有的話原樣回傳。</summary>
    /// <remarks>
    /// <c>IF EXISTS (SELECT …)</c> 那種流程控制不會誤傷：剝完是空字串或另一個
    /// 語句的尾巴，兩者都推不出目標，結果與剝之前一樣是 <see cref="CompletionTarget.Any"/>。
    /// </remarks>
    private static string TrimTrailingIfExists(string text)
    {
        return EndsWithKeywords(text, "IF", "EXISTS", out var start)
            ? text.Substring(0, start).TrimEnd()
            : text;
    }

    private static bool EndsWithKeywords(string text, string first, string second, out int keywordStart)
    {
        keywordStart = -1;
        var secondStart = FindPreviousTokenStart(text, text.Length);
        var secondToken = text.Substring(secondStart);
        var beforeSecond = text.Substring(0, secondStart).TrimEnd();
        var firstStart = FindPreviousTokenStart(beforeSecond, beforeSecond.Length);
        var firstToken = beforeSecond.Substring(firstStart);

        if (!string.Equals(firstToken, first, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(secondToken, second, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        keywordStart = firstStart;
        return true;
    }

    private static bool EndsWithKeyword(string text, string keyword, out int keywordStart)
    {
        var tokenStart = FindPreviousTokenStart(text, text.Length);

        if (!string.Equals(text.Substring(tokenStart), keyword, StringComparison.OrdinalIgnoreCase))
        {
            keywordStart = -1;
            return false;
        }

        keywordStart = tokenStart;
        return true;
    }

    /// <summary>
    /// 剝掉游標前方的限定字，回傳它的完整位置。
    /// </summary>
    /// <param name="beforeQualifier">
    /// 整串限定字<b>之前</b>的文字，供 <see cref="DetermineTarget"/> 判斷位置。
    /// 沒有限定字或限定字不合法時等於原文。
    /// </param>
    /// <remarks>
    /// 一路往左剝，不是只剝一段：<c>LibArchive.dbo.</c> 與 <c>dbo.</c> 在文字上只差
    /// 一段，要的東西卻在不同的資料庫裡。只剝一段有兩個症狀，而兩個都沒有徵兆——
    /// 清單列出目前連線的 dbo 物件，而 <paramref name="beforeQualifier"/> 停在
    /// <c>FROM LibArchive.</c> 上，位置判斷連 <c>FROM</c> 都看不到。
    ///
    /// 超過上限就整個不認。取最右邊三段的話，使用者打錯的一串名稱會安靜地
    /// 變成一個查得到的東西。
    /// </remarks>
    private static SqlObjectPath? ExtractQualifierPath(string text, out string beforeQualifier)
    {
        beforeQualifier = text;

        if (!text.EndsWith(".", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = new List<string>(SqlObjectPath.MaximumQualifierParts);
        var remaining = text;

        // 多讀一段才停，好讓超出上限的情形被 TryParseQualifier 擋下來而不是悄悄截短。
        while (remaining.EndsWith(".", StringComparison.Ordinal) &&
               parts.Count <= SqlObjectPath.MaximumQualifierParts)
        {
            var beforeDot = remaining.Substring(0, remaining.Length - 1).TrimEnd();

            if (beforeDot.EndsWith("]", StringComparison.Ordinal))
            {
                var openingBracket = beforeDot.LastIndexOf('[', beforeDot.Length - 1);

                if (openingBracket < 0)
                {
                    break;
                }

                parts.Insert(0, beforeDot
                    .Substring(openingBracket + 1, beforeDot.Length - openingBracket - 2)
                    .Replace("]]", "]"));
                remaining = beforeDot.Substring(0, openingBracket).TrimEnd();
                continue;
            }

            // 空段是 LibArchive.. 這種省略結構描述的寫法。每一圈至少吃掉一個點號，
            // 所以空段不會讓迴圈停不下來。
            var qualifierStart = FindPreviousTokenStart(beforeDot, beforeDot.Length);
            parts.Insert(0, beforeDot.Substring(qualifierStart));
            remaining = beforeDot.Substring(0, qualifierStart).TrimEnd();
        }

        if (!SqlObjectPath.TryParseQualifier(parts, out var path))
        {
            return null;
        }

        beforeQualifier = remaining;
        return path;
    }

    /// <summary>
    /// 這個字元可不可以構成識別字的一部分。
    /// </summary>
    /// <remarks>
    /// 公開出來是為了讓「要不要重開建議清單」的判斷用同一套字元分類。
    /// 那個判斷的前提正是「使用者剛輸入的字元結束了前一個詞元」，
    /// 兩邊各寫一份的話，分岔的症狀是某些字元之後清單該開卻不開。
    /// </remarks>
    public static bool IsIdentifierCharacter(char value) => IsTokenCharacter(value);

    private static int FindTokenStart(string text)
    {
        return FindPreviousTokenStart(text, text.Length);
    }

    private static int FindPreviousTokenStart(string text, int end)
    {
        var index = end;

        while (index > 0 && IsTokenCharacter(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    /// <remarks>
    /// 小老鼠算在內，而且必須算在內：<c>@@ROW</c> 的詞元起點要落在第一個小老鼠上，
    /// 否則適用範圍只蓋住 <c>ROW</c>，提交 <c>@@ROWCOUNT</c> 之後編輯器裡會留下
    /// <c>@@@@ROWCOUNT</c>。變數名稱同理。
    /// </remarks>
    private static bool IsTokenCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '#' || value == '@';
    }
}
