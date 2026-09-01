using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// 針對模組（預存程序、函式、檢視、觸發程序）原始定義的文字處理。
/// </summary>
public static class SqlModuleScript
{
    /// <summary>先切這麼多字元找標頭；不夠時才切完整份定義。</summary>
    private const int HeaderProbeLength = 1024;

    /// <summary>
    /// 把定義開頭的 <c>CREATE</c> 或 <c>CREATE OR ALTER</c> 改寫成 <c>ALTER</c>，
    /// 讓取回的定義可以直接執行以更新該模組。
    /// </summary>
    /// <remarks>
    /// 只改寫開頭的那一個關鍵字，定義本體完全不動：主體裡可能有 CREATE TABLE #tmp
    /// 之類的語句，全域取代會把它們一併破壞。開頭的註解與空白會被保留。
    /// </remarks>
    /// <returns>成功改寫或原本就是 ALTER 時為 true。</returns>
    public static bool TryConvertCreateToAlter(string definition, out string result)
    {
        result = definition;

        if (string.IsNullOrWhiteSpace(definition))
        {
            return false;
        }

        if (!TryReadWord(definition, 0, out var firstStart, out var firstEnd))
        {
            return false;
        }

        var first = definition.Substring(firstStart, firstEnd - firstStart);

        if (string.Equals(first, "ALTER", StringComparison.OrdinalIgnoreCase))
        {
            return true; // 已經是 ALTER，維持原樣。
        }

        if (!string.Equals(first, "CREATE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var replaceEnd = firstEnd;

        // CREATE OR ALTER：三個關鍵字一起換成單一個 ALTER。
        if (TryReadWord(definition, firstEnd, out var secondStart, out var secondEnd) &&
            string.Equals(definition.Substring(secondStart, secondEnd - secondStart), "OR", StringComparison.OrdinalIgnoreCase) &&
            TryReadWord(definition, secondEnd, out var thirdStart, out var thirdEnd) &&
            string.Equals(definition.Substring(thirdStart, thirdEnd - thirdStart), "ALTER", StringComparison.OrdinalIgnoreCase))
        {
            replaceEnd = thirdEnd;
        }

        result = definition.Substring(0, firstStart) + "ALTER" + definition.Substring(replaceEnd);
        return true;
    }

    /// <summary>
    /// 找出模組標頭（<c>ALTER PROCEDURE dbo.usp_Test</c>）裡物件名稱結束的位置。
    /// </summary>
    /// <remarks>
    /// 展開完整定義之後游標停在這裡而不是整段的結尾：使用者要看的是自己剛選的那個
    /// 名稱與它的參數，停在結尾等於一展開就被捲到定義的最後一行，得自己捲回去。
    ///
    /// 位置要在<b>改寫之後</b>的文字上算。<c>CREATE OR ALTER</c> 併成一個 <c>ALTER</c>
    /// 會讓後面每一個字元往前位移，在原始定義上算出來的位置會落在名稱中間。
    /// </remarks>
    /// <returns>名稱結束的位置；認不出標頭時回傳 -1，由呼叫端自己決定退回哪裡。</returns>
    public static int FindHeaderNameEnd(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return -1;
        }

        // 標頭一定在最前面，而定義動輒數萬字元——這一段跑在 UI 執行緒上，
        // 為了三個詞元把整份切完是白付的代價。名稱被超長的開頭註解推出視窗
        // （end < 0）或剛好被視窗切斷（end 貼著邊界）時才再切一次完整的。
        var probe = Math.Min(HeaderProbeLength, script.Length);
        var end = FindHeaderNameEnd(script, probe);

        return (end < 0 || end >= probe) && probe < script.Length
            ? FindHeaderNameEnd(script, script.Length)
            : end;
    }

    private static int FindHeaderNameEnd(string script, int limit)
    {
        var tokens = SqlTokenizer.Tokenize(script, 0, limit);
        var index = 0;

        if (!IsKeyword(tokens, index, "CREATE") && !IsKeyword(tokens, index, "ALTER"))
        {
            return -1;
        }

        index++;

        if (IsKeyword(tokens, index, "OR") && IsKeyword(tokens, index + 1, "ALTER"))
        {
            index += 2;
        }

        // 物件種類（PROCEDURE、PROC、FUNCTION、TRIGGER、VIEW…）跳過就好，不比對字面值：
        // 比對就要維護一份清單，而漏掉一種的症狀是那一種物件安靜地退回停在結尾。
        index++;

        var nameEnd = -1;

        while (index < tokens.Count && tokens[index].Kind == SqlTokenKind.Identifier)
        {
            nameEnd = tokens[index].End;
            index++;

            if (index >= tokens.Count || !tokens[index].IsPunctuation("."))
            {
                break;
            }

            // 連續的點號代表中間那一段省略了（LibraryDb..usp_Test），不是名稱結束。
            while (index < tokens.Count && tokens[index].IsPunctuation("."))
            {
                index++;
            }
        }

        return nameEnd;
    }

    private static bool IsKeyword(IReadOnlyList<SqlToken> tokens, int index, string keyword)
    {
        return index < tokens.Count && tokens[index].IsKeyword(keyword);
    }

    /// <summary>讀出從 <paramref name="index"/> 起的下一個單字，略過空白與註解。</summary>
    private static bool TryReadWord(string text, int index, out int start, out int end)
    {
        start = 0;
        end = 0;
        var position = SqlTrivia.Skip(text, index, text.Length);

        if (position >= text.Length || !IsWordCharacter(text[position]))
        {
            return false;
        }

        start = position;

        while (position < text.Length && IsWordCharacter(text[position]))
        {
            position++;
        }

        end = position;
        return true;
    }

    private static bool IsWordCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }
}
