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
/// （判錯就等於那個位置什麼都打不出來），只收<b>看得出來</b>的寫法，
/// 其餘一律照常，寧可少認幾個位置。
///
/// 新資料行的名稱之後不自己認形狀：名稱前面那一格是不是資料行定義的開頭，問位置分析
/// （<see cref="SqlKeywordPosition.ColumnDefinition"/>、<see cref="SqlKeywordPosition.AlterTableAdd"/>、
/// <see cref="SqlKeywordPosition.ResultSetColumn"/>）。
/// 各認一份的症狀是 CREATE TABLE 認得、ALTER TABLE ADD 認不得。
///
/// 沒有做成 <see cref="SqlKeywordPosition"/> 的一個新成員：那個列舉的每個成員都對應
/// 產生器 <c>tools/SqlAssist.KeywordGenerator/Data/PositionTemplates.cs</c> 裡的一個樣板，而型別根本不在關鍵字目錄裡，
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

    /// <summary>以型別為底的物件：<c>CREATE</c> 這種物件的名稱之後，這個字接型別。</summary>
    /// <remarks><c>CREATE SEQUENCE s AS int</c>、<c>CREATE TYPE t FROM varchar(10)</c>。</remarks>
    private static readonly Dictionary<string, string> TypedObjects =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SEQUENCE"] = "AS",
            ["TYPE"] = "FROM"
        };

    /// <summary>
    /// 判斷 <paramref name="tokens"/> 的尾端之後是不是型別的位置。
    /// </summary>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="textBeforeToken">同一段原文；問位置分析時要用。</param>
    public static bool IsDataTypeSlot(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (textBeforeToken is null)
        {
            throw new ArgumentNullException(nameof(textBeforeToken));
        }

        return IsDataTypeSlot(tokens, tokens.Count - 1, textBeforeToken);
    }

    /// <summary>
    /// 判斷 <paramref name="last"/> 這個詞元之後是不是型別的位置。
    /// </summary>
    /// <remarks>
    /// 帶索引是為了限定字：<c>DECLARE @t dbo.|</c> 的最後兩個詞元是使用者自訂型別的
    /// 結構描述與點號，把它們跳過去問同一個問題，答案就是原本那個位置的答案。
    /// </remarks>
    private static bool IsDataTypeSlot(IReadOnlyList<SqlToken> tokens, int last, string textBeforeToken)
    {
        if (last < 0)
        {
            return false;
        }

        var token = tokens[last];

        if (token.IsPunctuation(".") && last >= 1 && IsBareIdentifier(tokens[last - 1]))
        {
            return IsDataTypeSlot(tokens, last - 2, textBeforeToken);
        }

        // DECLARE @rows |、DECLARE @a INT = NULL, @b |、CREATE PROCEDURE p @a int OUTPUT, @b |
        // ——變數落在宣告的位置上，它後面就只能是型別。
        if (token.Kind == SqlTokenKind.Variable)
        {
            return SqlScriptVariableSuggestions.IsDeclarationSlot(tokens, last);
        }

        if (token.Kind != SqlTokenKind.Identifier)
        {
            // CONVERT(|、TRY_CONVERT(|
            return token.IsPunctuation("(") &&
                last >= 1 &&
                IsBareIdentifier(tokens[last - 1]) &&
                (TypeFirstArgument.Contains(tokens[last - 1].Value) || OpensPartitionFunction(tokens, last));
        }

        // 加了方括號的只可能是名稱：SSMS 產生的指令碼一律寫 CREATE TABLE [dbo].[Loan]([LoanId] |。
        if (token.IsQuoted)
        {
            return IsNewColumn(tokens, last, textBeforeToken);
        }

        if (TypeIntroducers.Contains(token.Value))
        {
            return true;
        }

        if (NamesTypedObject(tokens, last))
        {
            return true;
        }

        if (token.IsKeyword("AS"))
        {
            return TypeFollowsAs(tokens, last);
        }

        // ALTER TABLE t ALTER COLUMN c |；DROP COLUMN c 之後不接型別。
        if (last >= 2 && tokens[last - 1].IsKeyword("COLUMN") && tokens[last - 2].IsKeyword("ALTER"))
        {
            return true;
        }

        return IsNewColumn(tokens, last, textBeforeToken);
    }

    /// <summary><paramref name="last"/> 是剛寫完的新資料行名稱，之後是它的型別。</summary>
    private static bool IsNewColumn(IReadOnlyList<SqlToken> tokens, int last, string textBeforeToken)
    {
        return NewColumnPosition(tokens, last, textBeforeToken) is
            SqlKeywordPosition.ColumnDefinition or SqlKeywordPosition.AlterTableAdd or SqlKeywordPosition.ResultSetColumn;
    }

    /// <summary>
    /// 在 <paramref name="tokens"/> 的尾端接得上型別那一組的 <c>AS</c>：之後是型別（<c>DECLARE @x |</c>、
    /// <c>CAST(@y |</c>），或這一格本身是型別、也能改寫成計算資料行（<c>CREATE TABLE t (a int, b |</c>）。
    /// </summary>
    /// <remarks>
    /// 與「<c>AS</c> 之後是型別」同一條判斷，只是把 <c>AS</c> 放在還沒寫的那一格問；各認一份的症狀是
    /// 型別認得、它前面的 <c>AS</c> 卻列不出來。宣告與資料行的 <c>AS</c> 寫在型別的位置上，型別清單
    /// 多列它；<c>CAST</c> 的那一格照子句列其他字，只有落在選取清單時才碰巧有別名的 <c>AS</c>——
    /// <c>SET @x = CAST(@y |</c> 就沒有。運算元還沒寫完（<c>CAST(|</c>、<c>CAST(@y + |</c>）時不列；
    /// <c>RESULT SETS</c> 的資料行沒有計算資料行。
    /// </remarks>
    /// <param name="tokens">游標<b>之前</b>、不含正在輸入的那個詞元的詞法單元。</param>
    /// <param name="textBeforeToken">同一段原文；問位置分析時要用。</param>
    public static bool AcceptsAs(IReadOnlyList<SqlToken> tokens, string textBeforeToken)
    {
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (textBeforeToken is null)
        {
            throw new ArgumentNullException(nameof(textBeforeToken));
        }

        var last = tokens.Count - 1;

        return last >= 0 &&
            EndsOperand(tokens[last]) &&
            (TypeFollowsAs(tokens, tokens.Count) ||
                NewColumnPosition(tokens, last, textBeforeToken) is
                    SqlKeywordPosition.ColumnDefinition or SqlKeywordPosition.AlterTableAdd);
    }

    /// <summary>
    /// 寫在 <paramref name="asIndex"/> 的 <c>AS</c> 之後是型別：<c>CAST(x AS</c> 這幾個函式的第一個
    /// <c>AS</c>，以及宣告的 <c>DECLARE @rows AS</c>、<c>CREATE PROCEDURE p @x AS</c>。
    /// </summary>
    /// <remarks>只讀 <paramref name="asIndex"/> 之前的詞元，所以那一格的 <c>AS</c> 可以還沒寫。</remarks>
    private static bool TypeFollowsAs(IReadOnlyList<SqlToken> tokens, int asIndex)
    {
        if (asIndex >= 1 &&
            tokens[asIndex - 1].Kind == SqlTokenKind.Variable &&
            SqlScriptVariableSuggestions.IsDeclarationSlot(tokens, asIndex - 1))
        {
            return true;
        }

        var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, asIndex - 1);

        return open >= 1 &&
            IsBareIdentifier(tokens[open - 1]) &&
            TypeAfterAs.Contains(tokens[open - 1].Value) &&
            !HasAs(tokens, open + 1, asIndex);
    }

    /// <summary>
    /// <paramref name="start"/> 到 <paramref name="end"/> 之間、不在內層括號裡的地方已經寫過 <c>AS</c>。
    /// </summary>
    /// <remarks><c>CAST(x AS int |</c> 的型別已經寫了，不能再接一個 <c>AS</c>。</remarks>
    private static bool HasAs(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].IsPunctuation("("))
            {
                var close = SqlTokenNavigator.FindClosingParenthesis(tokens, index, end);

                if (close < 0)
                {
                    return false;
                }

                index = close;
                continue;
            }

            if (tokens[index].IsKeyword("AS"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>這個詞元寫完一個運算元：名稱、變數、常值、<c>)</c> 或 <c>CASE … END</c> 的 <c>END</c>。</summary>
    private static bool EndsOperand(SqlToken token)
    {
        return token.Kind switch
        {
            SqlTokenKind.Variable or SqlTokenKind.Number or SqlTokenKind.String => true,
            SqlTokenKind.Identifier => token.IsQuoted ||
                !SqlKeywordCatalog.IsKeyword(token.Value) ||
                token.IsKeyword("END") ||
                SqlKeywordCatalog.EndsItem(token.Value),
            _ => token.IsPunctuation(")")
        };
    }

    /// <summary>
    /// <paramref name="last"/> 是 <c>CREATE SEQUENCE s AS</c>、<c>CREATE TYPE t FROM</c> 名稱之後的那個字。
    /// </summary>
    private static bool NamesTypedObject(IReadOnlyList<SqlToken> tokens, int last)
    {
        if (last < 3 || !IsBareIdentifier(tokens[last - 1]))
        {
            return false;
        }

        var name = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, last - 1);

        return name >= 2 &&
            IsBareIdentifier(tokens[name - 1]) &&
            TypedObjects.TryGetValue(tokens[name - 1].Value, out var introducer) &&
            tokens[last].IsKeyword(introducer) &&
            tokens[name - 2].IsKeyword("CREATE");
    }

    /// <summary>
    /// <paramref name="open"/> 是 <c>CREATE PARTITION FUNCTION pf (</c> 的左括號：唯一的參數只寫型別、沒有名稱。
    /// </summary>
    private static bool OpensPartitionFunction(IReadOnlyList<SqlToken> tokens, int open)
    {
        var name = SqlTokenNavigator.SkipQualifiedNameBackward(tokens, open - 1);

        return name >= 3 &&
            tokens[name - 1].IsKeyword("FUNCTION") &&
            tokens[name - 2].IsKeyword("PARTITION") &&
            tokens[name - 3].IsKeyword("CREATE");
    }

    /// <summary>
    /// <paramref name="last"/> 可能是剛寫完的新資料行名稱時，回傳名稱前面那一格的位置；否則
    /// <see cref="SqlKeywordPosition.None"/>。是的話那一格是資料行定義的開頭：<c>CREATE TABLE dbo.Loan (LoanId |</c>、
    /// <c>DECLARE @t TABLE (Id INT, Name |</c>、<c>ALTER TABLE t ADD ReaderId |</c>、
    /// <c>EXEC p WITH RESULT SETS ((Branch |</c>。
    /// </summary>
    /// <remarks>
    /// 名稱前面那一格要是資料行定義的開頭，判準是位置分析的。只在名稱緊接著 <c>(</c>、逗號或
    /// <c>ADD</c> 時問——其餘的名稱前面不可能是那兩個位置，不必每一鍵都多分析一次。
    /// </remarks>
    private static SqlKeywordPosition NewColumnPosition(IReadOnlyList<SqlToken> tokens, int last, string textBeforeToken)
    {
        if (last < 1 || tokens[last].Kind != SqlTokenKind.Identifier ||
            (!tokens[last].IsQuoted && SqlKeywordCatalog.IsKeyword(tokens[last].Value)))
        {
            return SqlKeywordPosition.None;
        }

        var previous = tokens[last - 1];

        if (!previous.IsPunctuation("(") && !previous.IsPunctuation(",") && !previous.IsKeyword("ADD"))
        {
            return SqlKeywordPosition.None;
        }

        return SqlKeywordPositionAnalyzer.PositionBefore(tokens, last, textBeforeToken);
    }

    private static bool IsBareIdentifier(SqlToken token)
    {
        return token.Kind == SqlTokenKind.Identifier && !token.IsQuoted;
    }
}
