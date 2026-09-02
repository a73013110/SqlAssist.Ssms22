using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 游標是不是停在一個文法上只接受資料型別的位置。
/// </summary>
/// <remarks>
/// 這裡列的每一種都是「除了型別以外沒有別的東西是對的」，因此判定成立時整份清單
/// 就只剩型別——關鍵字、資料表、片段一個都不列。也因為代價是那麼直接
/// （判錯就等於那個位置什麼都打不出來），只收<b>看得出來</b>的六種寫法，
/// 其餘一律照常，寧可少認幾個位置。
///
/// 沒有做成 <see cref="SqlKeywordPosition"/> 的一個新成員：那個列舉的每個成員都對應
/// <c>tools/Generate-Keywords.ps1</c> 裡的一個樣板，而型別根本不在關鍵字目錄裡，
/// 加一個沒有樣板的成員只會讓兩邊對不起來。這裡要的是「換一份清單」而不是
/// 「篩掉一些關鍵字」，那正是 <see cref="CompletionTarget"/> 的工作。
/// </remarks>
public static class SqlDataTypePosition
{
    /// <summary>
    /// 這個字之後接的是型別，一個詞元就決定得了。
    /// </summary>
    /// <remarks>
    /// <c>RETURNS</c> 之後是純量函式的回傳型別（資料表值函式接的是
    /// <c>TABLE</c>，那也在型別清單裡）。
    /// </remarks>
    private static readonly HashSet<string> TypeIntroducers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "RETURNS"
        };

    /// <summary>括號直接接在這些字後面時，第一個引數是型別。</summary>
    /// <remarks><c>CONVERT(type, expression)</c>；<c>CAST</c> 走的是 <c>AS</c> 那一支。</remarks>
    private static readonly HashSet<string> TypeFirstArgument =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CONVERT", "TRY_CONVERT"
        };

    /// <summary>這些函式的 <c>AS</c> 之後是型別。</summary>
    private static readonly HashSet<string> TypeAfterAs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CAST", "TRY_CAST", "PARSE", "TRY_PARSE"
        };

    /// <summary>
    /// 判斷 <paramref name="tokens"/> 的尾端之後是不是型別的位置。
    /// </summary>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    public static bool IsDataTypeSlot(IReadOnlyList<SqlToken> tokens)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        return IsDataTypeSlot(tokens, tokens.Count - 1);
    }

    /// <summary>
    /// 判斷 <paramref name="last"/> 這個詞元之後是不是型別的位置。
    /// </summary>
    /// <remarks>
    /// 帶索引是為了限定字：<c>DECLARE @t dbo.|</c> 的最後兩個詞元是使用者自訂型別的
    /// 結構描述與點號，把它們跳過去問同一個問題，答案就是原本那個位置的答案。
    /// </remarks>
    private static bool IsDataTypeSlot(IReadOnlyList<SqlToken> tokens, int last)
    {
        if (last < 0)
        {
            return false;
        }

        var token = tokens[last];

        if (token.IsPunctuation(".") && last >= 1 && IsBareIdentifier(tokens[last - 1]))
        {
            return IsDataTypeSlot(tokens, last - 2);
        }

        // DECLARE @rows |、DECLARE @a INT, @b |、CREATE PROCEDURE p @readerId |
        // ——變數落在宣告的位置上，它後面就只能是型別。
        if (token.Kind == SqlTokenKind.Variable)
        {
            return SqlScriptVariableSuggestions.IsDeclarationSlot(tokens, last);
        }

        if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
        {
            // CONVERT(|、TRY_CONVERT(|
            return token.IsPunctuation("(") &&
                last >= 1 &&
                IsBareIdentifier(tokens[last - 1]) &&
                TypeFirstArgument.Contains(tokens[last - 1].Value);
        }

        if (TypeIntroducers.Contains(token.Value))
        {
            return true;
        }

        // CAST(x AS |、PARSE(x AS |，以及 DECLARE @rows AS | 那種帶 AS 的宣告。
        if (token.IsKeyword("AS"))
        {
            return IsInsideCall(tokens, last, TypeAfterAs) ||
                (last >= 1 &&
                    tokens[last - 1].Kind == SqlTokenKind.Variable &&
                    SqlScriptVariableSuggestions.IsDeclarationSlot(tokens, last - 1));
        }

        // ALTER TABLE t ALTER COLUMN c |
        if (last >= 1 && tokens[last - 1].IsKeyword("COLUMN"))
        {
            return true;
        }

        // CREATE TABLE dbo.Loan (LoanId |、DECLARE @t TABLE (Id INT, Name |
        return !SqlKeywordCatalog.IsKeyword(token.Value) && IsTableColumnSlot(tokens, last);
    }

    /// <summary>
    /// <paramref name="index"/> 落在某個函式呼叫的引數裡，而那個函式在
    /// <paramref name="names"/> 中。
    /// </summary>
    /// <remarks>
    /// 找的是還沒關上的那個左括號——使用者正在打的呼叫一定是還開著的那一個。
    /// 途中關得起來的括號整組跳過，它們是引數自己的。
    /// </remarks>
    private static bool IsInsideCall(
        IReadOnlyList<SqlToken> tokens,
        int index,
        HashSet<string> names)
    {
        var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, index - 1);

        return open >= 1 &&
            IsBareIdentifier(tokens[open - 1]) &&
            names.Contains(tokens[open - 1].Value);
    }

    /// <summary>
    /// 游標在 <c>CREATE TABLE</c> 或 <c>DECLARE @t TABLE</c> 的資料行清單裡，
    /// 而且剛打完一個資料行名稱。
    /// </summary>
    /// <remarks>
    /// 判斷從清單的左括號往回看，而不是從資料行名稱往回數：
    /// <c>INSERT INTO t (col1, col2)</c> 的括號長得一模一樣，
    /// 差別只在括號前面那個字是 <c>INTO</c> 的目標還是 <c>TABLE</c>。
    ///
    /// 資料行名稱前面必須是左括號或逗號。少了這一條，
    /// <c>CREATE TABLE t (Id INT NOT |</c> 也會被當成型別的位置。
    /// </remarks>
    private static bool IsTableColumnSlot(IReadOnlyList<SqlToken> tokens, int index)
    {
        if (index < 1)
        {
            return false;
        }

        var previous = tokens[index - 1];

        if (!previous.IsPunctuation("(") && !previous.IsPunctuation(","))
        {
            return false;
        }

        var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, index - 1);

        if (open < 1)
        {
            return false;
        }

        // CREATE TABLE dbo.Loan ( 的括號前面是帶點號的名稱，
        // DECLARE @t TABLE ( 的括號前面直接就是 TABLE。
        var before = open - 1;

        while (before >= 2 &&
               tokens[before - 1].IsPunctuation(".") &&
               IsBareIdentifier(tokens[before]))
        {
            before -= 2;
        }

        if (IsBareIdentifier(tokens[before]) && tokens[before].IsKeyword("TABLE"))
        {
            return true;
        }

        return before >= 1 && IsBareIdentifier(tokens[before - 1]) && tokens[before - 1].IsKeyword("TABLE");
    }

    private static bool IsBareIdentifier(SqlToken token)
    {
        return token.Kind == SqlTokenKind.Identifier && !token.IsQuoted;
    }
}
