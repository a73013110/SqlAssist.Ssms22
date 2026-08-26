using System;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core;

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

        var prefix = textBeforeCaret.Substring(tokenStart);
        var beforeToken = textBeforeCaret.Substring(0, tokenStart).TrimEnd();
        var qualifier = ExtractQualifier(beforeToken, out var beforeQualifier);
        var target = DetermineTarget(
            qualifier is null ? beforeToken : beforeQualifier,
            out var targetKeywordStart,
            out var intent);
        var isValid = prefix.Length > 0 || target != CompletionTarget.Any || qualifier is not null;

        // 限定字之後（dbo.| 或 u.|）要的是名稱，關鍵字在那裡一個都不該出現，
        // 但這裡不用特別處理：限定字會讓 Target 收斂，關鍵字已經被目標過濾擋掉。
        var keywordPosition = SqlKeywordPositionAnalyzer.Analyze(
            textBeforeCaret.Substring(0, tokenStart));

        return new SqlCompletionContext(
            isValid,
            tokenStart,
            prefix,
            target,
            qualifier,
            targetKeywordStart,
            intent,
            qualifiedTable: null,
            keywordPosition);
    }

    /// <summary>
    /// 分析整份文字中游標所在的位置，並在限定字指向敘述內的資料來源時
    /// 把建議目標改成欄位。
    /// </summary>
    /// <remarks>
    /// 必須看得到游標後方的文字：<c>SELECT u.| FROM dbo.Lib_Reader u</c> 這種
    /// 編輯既有查詢的情形，FROM 子句在游標之後，只看前文永遠解析不出 <c>u</c>。
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

        if (context.Qualifier is null)
        {
            return context;
        }

        // 前方關鍵字已經指定了物件類別（FROM、JOIN、EXEC…），代表游標正在輸入
        // 資料來源本身，此時點號前面必然是結構描述而不是別名：
        // FROM dbo.| 要列出 dbo 的物件，FROM u.| 這種寫法並不存在。
        if (context.Target != CompletionTarget.Any)
        {
            return context;
        }

        var scope = SqlScopeAnalyzer.Analyze(sql, caretPosition);

        // 衍生資料表與資料表變數查不到欄位中繼資料，維持原本的結構描述解讀，
        // 讓使用者至少還看得到物件清單。
        if (!scope.TryResolve(context.Qualifier, out var table) || table.IsDerived)
        {
            return context;
        }

        return context.AsColumnsOf(table);
    }

    /// <summary>
    /// 依游標前方的關鍵字判斷應該建議哪一類物件，並回報該關鍵字的起點。
    /// </summary>
    private static CompletionTarget DetermineTarget(
        string text,
        out int keywordStart,
        out CompletionIntent intent)
    {
        // ALTER 之後要放進完整定義，因此與 EXEC 之類的單純參考分開表示。
        intent = CompletionIntent.AlterDefinition;

        if (EndsWithKeywords(text, "ALTER", "PROCEDURE", out keywordStart))
        {
            return CompletionTarget.Procedure;
        }

        if (EndsWithKeywords(text, "ALTER", "FUNCTION", out keywordStart))
        {
            return CompletionTarget.Function;
        }

        intent = CompletionIntent.Reference;

        if (EndsWithKeyword(text, "EXEC", out keywordStart) ||
            EndsWithKeyword(text, "EXECUTE", out keywordStart))
        {
            return CompletionTarget.Procedure;
        }

        if (EndsWithKeyword(text, "USE", out keywordStart))
        {
            return CompletionTarget.Database;
        }

        if (EndsWithKeyword(text, "FROM", out keywordStart) ||
            EndsWithKeyword(text, "JOIN", out keywordStart) ||
            EndsWithKeyword(text, "UPDATE", out keywordStart) ||
            EndsWithKeyword(text, "INTO", out keywordStart))
        {
            return CompletionTarget.DataSource;
        }

        keywordStart = -1;
        return CompletionTarget.Any;
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

    private static string? ExtractQualifier(string text, out string beforeQualifier)
    {
        beforeQualifier = text;

        if (!text.EndsWith(".", StringComparison.Ordinal))
        {
            return null;
        }

        var beforeDot = text.Substring(0, text.Length - 1).TrimEnd();

        if (beforeDot.EndsWith("]", StringComparison.Ordinal))
        {
            var openingBracket = beforeDot.LastIndexOf('[', beforeDot.Length - 1);

            if (openingBracket >= 0)
            {
                beforeQualifier = beforeDot.Substring(0, openingBracket).TrimEnd();
                return beforeDot
                    .Substring(openingBracket + 1, beforeDot.Length - openingBracket - 2)
                    .Replace("]]", "]");
            }
        }

        var qualifierStart = FindPreviousTokenStart(beforeDot, beforeDot.Length);
        beforeQualifier = beforeDot.Substring(0, qualifierStart).TrimEnd();
        var qualifier = beforeDot.Substring(qualifierStart);
        return qualifier.Length == 0 ? null : qualifier;
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

    private static bool IsTokenCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '#';
    }
}
