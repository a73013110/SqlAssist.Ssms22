using System;
using System.Collections.Generic;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Completion;

/// <summary>游標停在哪一個呼叫的引數清單裡，現在輪到第幾個引數。</summary>
public sealed class SqlCallSignatureContext
{
    public SqlCallSignatureContext(
        int nameStart,
        int nameEnd,
        int openParenthesis,
        int argumentIndex,
        bool isQualified = false)
    {
        NameStart = nameStart;
        NameEnd = nameEnd;
        OpenParenthesis = openParenthesis;
        ArgumentIndex = argumentIndex;
        IsQualified = isQualified;
    }

    /// <summary>
    /// 名稱<b>最後一段</b>的起點。
    /// </summary>
    /// <remarks>
    /// 刻意不含限定字：這個位置是要交給 <c>SqlIdentifierScanner</c>／<c>SqlObjectLookup</c>
    /// 再解析一次的，而那一份本來就會自己往左收限定字，跨資料庫的三段式名稱也只認一次。
    /// 在這裡先切一段出來的話，同一個名稱會有兩份切法。
    /// </remarks>
    public int NameStart { get; }

    public int NameEnd { get; }

    /// <summary>左括號的位置；提示錨在它身上，而不是錨在游標上。</summary>
    public int OpenParenthesis { get; }

    /// <summary>目前輪到第幾個引數，0 起算。</summary>
    public int ArgumentIndex { get; }

    /// <summary>
    /// 名稱前面有限定字（<c>dbo.f(</c>）。
    /// </summary>
    /// <remarks>
    /// 純量函式在 T-SQL 裡一定要寫結構描述，所以沒有限定字的呼叫不可能是純量函式——
    /// 參數提示續接據此省掉一次中繼資料查詢，只交給 SSMS 那一份。
    /// </remarks>
    public bool IsQualified { get; }
}

/// <summary>
/// 游標是不是站在一個「名稱(…」的引數清單裡。
/// </summary>
/// <remarks>
/// 只回答文字看得出來的三件事：哪個名稱、哪個左括號、第幾個引數。
/// 那個名稱是不是純量函式、參數叫什麼，都要問中繼資料，不在這一層。
///
/// 與 <see cref="SqlArgumentPosition"/> 分開：那一支回答的是「這個位置只有幾個字合法」，
/// 用來整份換掉建議清單，因此只收 <c>DATEADD</c>、<c>WITH</c>、<c>OPTION</c> 三種認得出來
/// 的字面值；這一支要對<b>任何</b>名稱都成立，答案也不改變清單。
/// </remarks>
public static class SqlCallSignature
{
    /// <param name="sql">整份指令碼。</param>
    /// <param name="caret">游標位置。</param>
    /// <param name="includeKeywordFunctions">
    /// 名稱同時是關鍵字的內建函式（<c>CONVERT</c>、<c>LEFT</c>、<c>COALESCE</c>）也算呼叫。
    /// 要查中繼資料的呼叫端不要：那些名稱查不到物件，只是白付一次查詢。
    /// 參數提示續接要：它不查物件，只問「游標是不是站在某個呼叫的括號裡」，
    /// 而 SSMS 那一份參數資訊正好涵蓋這幾個字。
    /// </param>
    /// <returns>不在任何引數清單裡時為 null。</returns>
    public static SqlCallSignatureContext? Resolve(string sql, int caret, bool includeKeywordFunctions = false)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (caret < 0 || caret > sql.Length)
        {
            return null;
        }

        // 字串與註解裡的括號是內容不是語法；那裡的逗號也不是引數分隔。
        if (!SqlLexicalContext.IsCode(sql, caret))
        {
            return null;
        }

        var textBeforeCaret = sql.Substring(0, caret);
        var tokens = SqlTokenizer.Tokenize(textBeforeCaret);
        var open = SqlTokenNavigator.FindUnclosedParenthesis(tokens, tokens.Count - 1);

        if (!IsCall(tokens, open, includeKeywordFunctions))
        {
            return null;
        }

        var name = tokens[open - 1];
        var qualified = IsQualified(tokens, open);

        return new SqlCallSignatureContext(
            name.Start,
            name.End,
            tokens[open].Start,
            CountArguments(tokens, open),
            qualified);
    }

    /// <summary>
    /// 已知左括號在哪裡時，游標現在落在第幾個引數上。
    /// </summary>
    /// <remarks>
    /// 提示開著的期間每一次游標移動與每一次編輯都要問一次，所以這一支只吃
    /// <b>左括號到游標</b>那一段原文，不重新分析整份指令碼——<see cref="Resolve"/>
    /// 要從游標往回找左括號，非掃過前面不可，而這裡的起點已經知道了。
    /// 幾千行的指令碼裡每按一鍵重掃一次（連取出整份文字都算）的代價，
    /// 正是打字時看得出來的那種延遲。
    /// </remarks>
    /// <param name="argumentList">
    /// 左括號（含）到游標（不含）之間的原文。起點通常來自一個 <c>ITrackingPoint</c>，
    /// 所以前面的文字改過之後仍然對得上。
    /// </param>
    /// <returns>
    /// 引數序號，0 起算；游標已經不在這一組括號裡（括號被刪掉、或已經收在游標之前），
    /// 或走進了引數裡的另一個呼叫時為 null，呼叫端據此把提示收掉。
    /// </returns>
    /// <remarks>
    /// 引數裡的呼叫（<c>dbo.dtoc(GETDATE(|))</c>）歸內層那一份：游標在那裡時外層的
    /// 提示還開著，就是兩份簽章疊在一起，而使用者正在填的是內層的引數。
    /// 分組括號與子查詢（<c>dbo.f((1 + |</c>）不是呼叫，外層照樣留著。
    /// </remarks>
    public static int? TrackArgument(string argumentList)
    {
        if (argumentList is null)
        {
            throw new ArgumentNullException(nameof(argumentList));
        }

        // 起點上不再是左括號：那個字被刪掉或被換掉了，這一組括號已經不存在。
        if (argumentList.Length == 0 || argumentList[0] != '(')
        {
            return null;
        }

        var tokens = SqlTokenizer.Tokenize(argumentList);

        // 還沒收起來的內層括號，各自記著是不是呼叫；堆疊的深度就是巢狀層數。
        var nested = new Stack<bool>();
        var count = 0;

        // 第 0 個是那個左括號本身。
        for (var index = 1; index < tokens.Count; index++)
        {
            if (tokens[index].IsPunctuation("("))
            {
                nested.Push(IsCall(tokens, index, includeKeywordFunctions: true));
            }
            else if (tokens[index].IsPunctuation(")"))
            {
                // 這一層已經收起來了，游標站在整個呼叫外面。
                if (nested.Count == 0)
                {
                    return null;
                }

                nested.Pop();
            }
            else if (nested.Count == 0 && tokens[index].IsPunctuation(","))
            {
                count++;
            }
        }

        return nested.Contains(true) ? null : count;
    }

    /// <summary>
    /// <paramref name="open"/> 那個左括號是不是一個呼叫的開頭。
    /// </summary>
    private static bool IsCall(IReadOnlyList<SqlToken> tokens, int open, bool includeKeywordFunctions)
    {
        // open == 0 也不行：左括號前面沒有東西時那是分組括號，不是呼叫。
        if (open < 1)
        {
            return false;
        }

        var name = tokens[open - 1];

        // 名稱必須緊貼著左括號。dbo.f (1 仍然算——中間只有空白，T-SQL 也認；
        // 但 (1 + 2) * (3 這種前面站著運算子或另一個括號的一律不是呼叫。
        if (name.Kind != SqlTokenKind.Identifier)
        {
            return false;
        }

        // WHERE (、VALUES (、IN (、CONVERT (…：關鍵字後面的括號是語法的一部分或是
        // 內建函式，兩種都不歸這裡管。加引號的名稱（[Values]）與有限定字的名稱
        // （dbo.Value）例外——那兩種寫法本身就已經在說「這是一個物件」，
        // 拿最後一段去比關鍵字清單會把它們一起擋掉。內建函式那一半由呼叫端決定要不要，
        // 見 includeKeywordFunctions。
        return IsQualified(tokens, open) ||
            name.IsQuoted ||
            !SqlKeywordCatalog.IsKeyword(name.Value) ||
            (includeKeywordFunctions && SqlFunctionCatalog.TryGetSignature(name.Value, out _));
    }

    private static bool IsQualified(IReadOnlyList<SqlToken> tokens, int open) =>
        open >= 2 && tokens[open - 2].IsPunctuation(".");

    /// <summary>左括號之後、同一層的逗號有幾個。</summary>
    /// <remarks>
    /// 只數同一層：<c>dbo.f(dbo.g(1, 2), |</c> 裡面那兩個逗號屬於 <c>g</c>，
    /// 數進來的話外層會停在一個根本不存在的第三個引數上。內層的括號用
    /// <see cref="SqlTokenNavigator.SkipParenthesised"/> 整段跳過，那一份與
    /// 欄位解析共用，各寫一份的下場是其中一份漏掉巢狀的情形。
    /// </remarks>
    private static int CountArguments(IReadOnlyList<SqlToken> tokens, int open)
    {
        var index = open + 1;
        var count = 0;

        while (index < tokens.Count)
        {
            if (tokens[index].IsPunctuation("("))
            {
                index = SqlTokenNavigator.SkipParenthesised(tokens, index, tokens.Count);
                continue;
            }

            if (tokens[index].IsPunctuation(","))
            {
                count++;
            }

            index++;
        }

        return count;
    }
}
