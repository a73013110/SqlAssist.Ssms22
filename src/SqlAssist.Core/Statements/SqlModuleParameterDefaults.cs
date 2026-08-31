using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Statements;

/// <summary>
/// 從模組定義找出哪些參數寫了預設值，因而呼叫時可以整個省略。
/// </summary>
/// <remarks>
/// 這一份只能從定義本文讀出來。<c>sys.parameters.has_default_value</c> 對 T-SQL 模組
/// <b>永遠是 0</b>——那一欄只對 CLR 模組有效，而中繼資料層拿得到的就只有那一欄。
/// 少了這一步，展開出來的 EXEC 會把七個參數全部列出來，而使用者根本分不出哪三個
/// 本來就不必傳。
///
/// 定義是第三層資料，本來不在按鍵路徑上；但提交建議也不在按鍵路徑上，而且
/// <c>GetDetailAsync</c> 的同一次呼叫本來就會把欄位、參數與定義一起帶回來，
/// 所以這裡不多付任何一次往返。
///
/// 讀不出來就回傳空集合：少標幾個「選擇性」只是少一點資訊，猜錯卻會讓使用者
/// 刪掉一個其實必填的參數。
/// </remarks>
public static class SqlModuleParameterDefaults
{
    private static readonly ISet<string> None =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>回傳有預設值的參數名稱（含 <c>@</c> 前綴）。</summary>
    /// <param name="definition">模組定義本文；加密模組為 null。</param>
    public static ISet<string> Find(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
        {
            return None;
        }

        var tokens = SqlTokenizer.Tokenize(definition!);
        var start = FindParameterListStart(tokens);

        if (start < 0)
        {
            return None;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;

        for (var index = start; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.IsPunctuation("("))
            {
                depth++;
                continue;
            }

            if (token.IsPunctuation(")"))
            {
                // 函式的參數清單自己就包在一對括號裡，收掉那一對就代表清單結束了。
                if (--depth < 0)
                {
                    break;
                }

                continue;
            }

            // 程序的參數清單以 AS 收尾，函式以 RETURNS。兩者都不可能出現在型別中間。
            if (depth == 0 && (token.IsKeyword("AS") || token.IsKeyword("RETURNS")))
            {
                break;
            }

            if (token.Kind == SqlTokenKind.Variable && HasDefault(tokens, index, depth))
            {
                names.Add(token.Value);
            }
        }

        return names.Count == 0 ? None : names;
    }

    /// <summary>
    /// 參數清單從模組名稱之後開始。
    /// </summary>
    /// <remarks>
    /// 認的是 <c>PROCEDURE</c>／<c>PROC</c>／<c>FUNCTION</c> 這個字本身，前面是
    /// <c>CREATE</c> 還是 <c>CREATE OR ALTER</c> 都不必看——那三個字在定義開頭
    /// 只會出現一次，而它後面接的必定是名稱。
    /// </remarks>
    private static int FindParameterListStart(IReadOnlyList<SqlToken> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].IsKeyword("PROCEDURE") ||
                tokens[index].IsKeyword("PROC") ||
                tokens[index].IsKeyword("FUNCTION"))
            {
                return index + 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// 這個參數名稱後面接的是不是預設值。
    /// </summary>
    /// <remarks>
    /// 往後找到<b>同一層</b>的第一個 <c>=</c> 或 <c>,</c> 就有答案：型別本身寫不出
    /// 這兩個符號，而 <c>decimal(18,2)</c> 的逗號在括號裡，深度不同所以不會誤判。
    /// </remarks>
    private static bool HasDefault(IReadOnlyList<SqlToken> tokens, int start, int depth)
    {
        var current = depth;

        for (var index = start + 1; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.IsPunctuation("("))
            {
                current++;
                continue;
            }

            if (token.IsPunctuation(")"))
            {
                if (--current < depth)
                {
                    return false;
                }

                continue;
            }

            if (current != depth)
            {
                continue;
            }

            if (token.IsPunctuation(","))
            {
                return false;
            }

            if (token.Kind == SqlTokenKind.Operator && string.Equals(token.Value, "=", StringComparison.Ordinal))
            {
                return true;
            }

            if (token.IsKeyword("AS") || token.IsKeyword("RETURNS"))
            {
                return false;
            }
        }

        return false;
    }
}
