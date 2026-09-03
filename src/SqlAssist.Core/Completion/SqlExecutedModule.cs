using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// <c>EXEC</c> 正在呼叫的那個模組。
/// </summary>
/// <remarks>
/// <c>EXEC dbo.usp_Renew @</c> 這個位置要的是<b>那個程序的參數名稱</b>，而參數只在
/// 中繼資料裡。Core 讀不到資料庫，因此這裡只負責回答「他在呼叫誰」，
/// 由 SSMS 那一層拿這個名字去換參數清單。
///
/// 使用者自己宣告的變數在同一個位置也是對的——<c>EXEC p @myVar</c> 是照順序傳值。
/// 兩份清單因此併在一起，參數排前面。
/// </remarks>
public sealed class SqlExecutedModule
{
    /// <summary>
    /// 引數清單裡出現得了、而且<b>不代表新敘述開始</b>的關鍵字。
    /// </summary>
    /// <remarks>
    /// 往回走時遇到別的關鍵字就代表中間夾了另一個敘述，那個 <c>EXEC</c> 早就結束了：
    /// <c>EXEC dbo.p</c> 換行之後的 <c>SELECT … WHERE x = @</c> 不該去撈 <c>p</c> 的參數。
    /// </remarks>
    private static readonly HashSet<string> ArgumentKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "OUTPUT", "OUT", "DEFAULT", "NULL"
        };

    private SqlExecutedModule(SqlObjectPath path)
    {
        Path = path;
    }

    /// <summary>被呼叫的模組的完整位置。</summary>
    public SqlObjectPath Path { get; }

    /// <summary>限定的結構描述；沒寫時為 null。</summary>
    public string? SchemaName => Path.SchemaName;

    public string ObjectName => Path.Name;

    /// <summary>這個模組在目前這條連線上查得到嗎。</summary>
    public bool IsLocal => Path.IsLocal;

    /// <summary>
    /// 從游標前方的詞元找出正在呼叫的模組；不在 <c>EXEC</c> 的引數清單裡就回傳 null。
    /// </summary>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <remarks>
    /// 往回跳過已經打好的引數，落點必須剛好是 <c>EXEC</c>／<c>EXECUTE</c>。
    /// 這比「往回找最近的 EXEC」嚴格，而嚴格正是重點：只要中間夾著任何一個別的
    /// 關鍵字，那就是另一個敘述，撈回來的參數清單會是別人的。
    /// </remarks>
    public static SqlExecutedModule? Find(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var index = tokens.Count - 1;

        while (index >= 0 && IsArgumentToken(tokens[index]))
        {
            index--;
        }

        if (index < 0 ||
            (!tokens[index].IsKeyword("EXEC") && !tokens[index].IsKeyword("EXECUTE")))
        {
            return null;
        }

        return ParseName(tokens, index + 1);
    }

    /// <summary>
    /// 讀 <c>EXEC</c> 後面那個一到四段的名稱。
    /// </summary>
    /// <remarks>
    /// 曾經只取後兩段，理由是「中繼資料只看得到目前連線的資料庫，取後兩段至少讓
    /// 同名的那一個對得上」。那個理由不成立：對得上的是<b>另一個</b>程序，
    /// 而參數清單長得不一樣時，使用者按著提示填完的每一個引數都是錯的。
    /// 現在整串留著，由取參數的那一層決定查不查得到。
    ///
    /// <c>EXEC ('SELECT 1')</c> 與 <c>EXEC @procName</c> 讀不出名稱，回傳 null。
    /// </remarks>
    private static SqlExecutedModule? ParseName(IReadOnlyList<SqlToken> tokens, int start)
    {
        var parts = new List<string>(SqlObjectPath.MaximumNameParts);
        var index = start;

        while (index < tokens.Count)
        {
            if (tokens[index].Kind == SqlTokenKind.Identifier &&
                (tokens[index].IsQuoted || !SqlKeywordCatalog.IsKeyword(tokens[index].Value)))
            {
                parts.Add(tokens[index].Value);
                index++;
            }
            else if (parts.Count > 0 && tokens[index].IsPunctuation("."))
            {
                // db..p 這種寫法中間那段是空的；補一個空段而不是跳過，
                // 右對齊時位置才對得回去。
                parts.Add(string.Empty);
            }
            else
            {
                break;
            }

            if (index < tokens.Count && tokens[index].IsPunctuation("."))
            {
                index++;
                continue;
            }

            break;
        }

        return SqlObjectPath.TryParseName(parts, out var path)
            ? new SqlExecutedModule(path!)
            : null;
    }

    private static bool IsArgumentToken(SqlToken token)
    {
        switch (token.Kind)
        {
            case SqlTokenKind.Variable:
            case SqlTokenKind.Number:
            case SqlTokenKind.String:
            case SqlTokenKind.Operator:
                return true;

            case SqlTokenKind.Punctuation:
                return token.IsPunctuation(",")
                    || token.IsPunctuation(".")
                    || token.IsPunctuation("(")
                    || token.IsPunctuation(")");

            case SqlTokenKind.Identifier:
                return token.IsQuoted
                    || !SqlKeywordCatalog.IsKeyword(token.Value)
                    || ArgumentKeywords.Contains(token.Value);

            default:
                return false;
        }
    }
}
