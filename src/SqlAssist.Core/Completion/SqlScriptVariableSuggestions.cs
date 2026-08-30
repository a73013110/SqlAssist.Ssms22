using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 指令碼自己宣告的變數與參數。
/// </summary>
/// <remarks>
/// 與 CTE、暫存資料表同一條推理：這些名稱只存在於這份指令碼裡，中繼資料一個都
/// 看不到，而使用者會去補字正是因為那個名稱是他剛取的、還沒背起來。
///
/// 收的是<b>每一個</b>單小老鼠詞元，不分辨它出現在宣告還是使用的位置——理由與
/// 暫存資料表相同：<c>DECLARE</c>、程序參數、函式參數各認一次的話，漏掉的那一種
/// 寫法就會安靜地少一個名稱，而多收的那些本來就是使用者自己在這份指令碼裡打過的字。
/// 兩個小老鼠開頭的是系統的全域變數，那是另一份封閉的清單（
/// <see cref="SqlGlobalVariableCatalog"/>），不收在這裡。
/// </remarks>
public static class SqlScriptVariableSuggestions
{
    private const string VariableDescription = "變數";

    /// <summary>
    /// 往回走到這些字就代表使用者正在<b>宣告</b>一個名字。
    /// </summary>
    /// <remarks>
    /// <c>TABLE</c> 在裡面是為了 <c>DECLARE @t TABLE (…), @b INT</c>：往回跳過那組
    /// 括號之後遇到的是 <c>TABLE</c> 而不是 <c>DECLARE</c>。它不會誤判使用的位置——
    /// <c>FROM @t</c>、<c>INSERT INTO @t</c> 往回遇到的都是 <c>FROM</c>、<c>INTO</c>。
    /// </remarks>
    private static readonly HashSet<string> DeclarationAnchors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "DECLARE", "PROCEDURE", "FUNCTION", "TABLE"
        };

    /// <summary>
    /// 組出這份指令碼在游標之前宣告過的變數。
    /// </summary>
    /// <param name="tokens">整份指令碼的詞法單元。</param>
    /// <param name="caretPosition">游標位置。</param>
    /// <remarks>
    /// 只收<b>結束於游標之前</b>的詞元。少了這一條，使用者打到一半的 <c>@pu</c>
    /// 自己會出現在清單裡，而選它等於什麼都沒做。
    /// </remarks>
    public static IReadOnlyList<SqlSuggestion> Create(IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        List<SqlSuggestion>? suggestions = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.Kind != SqlTokenKind.Variable ||
                token.End >= caretPosition ||
                !IsLocalVariable(token.Value) ||
                !seen.Add(token.Value))
            {
                continue;
            }

            var description = DescribeType(tokens, index);

            (suggestions ??= new List<SqlSuggestion>()).Add(new SqlSuggestion(
                token.Value,
                token.Value,
                description,
                $"{token.Value}（{description}）",
                SuggestionKind.Variable));
        }

        return (IReadOnlyList<SqlSuggestion>?)suggestions ?? Array.Empty<SqlSuggestion>();
    }

    /// <summary>
    /// <paramref name="index"/> 這個位置文法上要的是一個<b>還沒存在</b>的名字。
    /// </summary>
    /// <param name="tokens">游標或該詞元<b>之前</b>的詞法單元。</param>
    /// <param name="index">要判斷的詞元索引；判斷從它的前一個詞元開始往回走。</param>
    /// <remarks>
    /// 分辨的是「他在宣告」與「他在引用」：<c>DECLARE @</c>、<c>DECLARE @a INT, @</c>、
    /// <c>CREATE PROCEDURE p @</c> 是前者，清單一項都不該出現——彈出來的唯一效果是
    /// 他順手按下 Enter，剛打的 <c>@pub</c> 被換成別的名字。<c>SET @</c>、
    /// <c>WHERE a = @</c>、<c>EXEC p @</c> 是後者，那裡他要的正是上面宣告過的名稱。
    ///
    /// 判斷方式是往回走到第一個關鍵字：是 <see cref="DeclarationAnchors"/> 裡的就是
    /// 宣告，是別的關鍵字（<c>SET</c>、<c>WHERE</c>、<c>EXEC</c>…）就是引用。
    /// 途中的括號整組跳過，分號代表前一個敘述已經結束。走到頭都沒有關鍵字時當成
    /// 引用：這裡的 fail-open 換來的是「多列幾個他自己打過的名字」。
    /// </remarks>
    public static bool IsDeclarationSlot(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        for (var current = Math.Min(index, tokens.Count) - 1; current >= 0; current--)
        {
            var token = tokens[current];

            if (token.IsPunctuation(")"))
            {
                var open = SqlTokenNavigator.FindOpeningParenthesis(tokens, current);

                if (open < 0)
                {
                    return false;
                }

                current = open;
                continue;
            }

            if (token.IsPunctuation(";"))
            {
                return false;
            }

            if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
            {
                continue;
            }

            if (DeclarationAnchors.Contains(token.Value))
            {
                return true;
            }

            if (SqlKeywordCatalog.IsKeyword(token.Value))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>單小老鼠開頭而且後面有名字。</summary>
    private static bool IsLocalVariable(string value)
    {
        return value.Length >= 2 && value[0] == '@' && value[1] != '@';
    }

    /// <summary>
    /// 宣告時寫的型別；不是宣告的位置就只寫「變數」。
    /// </summary>
    /// <remarks>
    /// 不含長度與有效位數。要組出 <c>NVARCHAR(50)</c> 得把括號裡的詞元再拼回字串，
    /// 而使用者在這份清單裡要挑的是名字，型別只是用來認人。
    /// </remarks>
    private static string DescribeType(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (!IsDeclarationSlot(tokens, index) || index + 1 >= tokens.Count)
        {
            return VariableDescription;
        }

        var next = tokens[index + 1];

        return next.Kind == SqlTokenKind.Identifier && !next.IsQuoted
            ? next.Value.ToUpperInvariant()
            : VariableDescription;
    }
}
