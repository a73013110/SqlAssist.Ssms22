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

    private SqlExecutedModule(string? schemaName, string objectName)
    {
        SchemaName = schemaName;
        ObjectName = objectName;
    }

    /// <summary>限定的結構描述；沒寫時為 null。</summary>
    public string? SchemaName { get; }

    public string ObjectName { get; }

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
    /// 讀 <c>EXEC</c> 後面那個一段或兩段的名稱。
    /// </summary>
    /// <remarks>
    /// 三段式的 <c>db.dbo.p</c> 取後兩段：中繼資料只看得到目前連線的資料庫，
    /// 而跨資料庫呼叫在那份清單上找不到，取後兩段至少讓同名的那一個對得上。
    /// <c>EXEC ('SELECT 1')</c> 與 <c>EXEC @procName</c> 讀不出名稱，回傳 null。
    /// </remarks>
    private static SqlExecutedModule? ParseName(IReadOnlyList<SqlToken> tokens, int start)
    {
        var parts = new List<string>(3);
        var index = start;

        while (index < tokens.Count &&
               tokens[index].Kind == SqlTokenKind.Identifier &&
               (tokens[index].IsQuoted || !SqlKeywordCatalog.IsKeyword(tokens[index].Value)))
        {
            parts.Add(tokens[index].Value);
            index++;

            if (index >= tokens.Count || !tokens[index].IsPunctuation("."))
            {
                break;
            }

            index++;
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var name = parts[parts.Count - 1];
        var schema = parts.Count >= 2 ? parts[parts.Count - 2] : null;
        return new SqlExecutedModule(schema, name);
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
