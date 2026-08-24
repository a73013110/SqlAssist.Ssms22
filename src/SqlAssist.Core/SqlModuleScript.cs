using System;

namespace SqlAssist.Core;

/// <summary>
/// 針對模組（預存程序、函式、檢視、觸發程序）原始定義的文字處理。
/// </summary>
public static class SqlModuleScript
{
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

    /// <summary>讀出從 <paramref name="index"/> 起的下一個單字，略過空白與註解。</summary>
    private static bool TryReadWord(string text, int index, out int start, out int end)
    {
        start = 0;
        end = 0;
        var position = SkipTrivia(text, index);

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

    private static int SkipTrivia(string text, int index)
    {
        var position = index;

        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            if (position + 1 < text.Length && text[position] == '-' && text[position + 1] == '-')
            {
                position += 2;

                while (position < text.Length && text[position] != '\n' && text[position] != '\r')
                {
                    position++;
                }

                continue;
            }

            if (position + 1 < text.Length && text[position] == '/' && text[position + 1] == '*')
            {
                position += 2;

                while (position + 1 < text.Length && !(text[position] == '*' && text[position + 1] == '/'))
                {
                    position++;
                }

                position = Math.Min(position + 2, text.Length);
                continue;
            }

            break;
        }

        return position;
    }

    private static bool IsWordCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }
}
