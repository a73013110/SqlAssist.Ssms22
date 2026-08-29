namespace SqlAssist.Core.Parsing;

/// <summary>
/// T-SQL 的空白與註解。
/// </summary>
/// <remarks>
/// 詞法分析與「讀出下一個關鍵字」這種文字層的判斷都要略過同一批東西，
/// 各寫一份的下場是兩邊對同一段文字的看法不同。實際發生過：
/// <see cref="SqlModuleScript"/> 自己那份只找第一個 <c>*/</c>，於是
/// <c>/* 註解 /* 巢狀 */ 還在註解裡 */ CREATE PROCEDURE …</c> 這種定義
/// 會被判成「開頭不是 CREATE」而放棄改寫成 ALTER，
/// <see cref="SqlTokenizer"/> 走同一段文字卻是對的。
/// </remarks>
public static class SqlTrivia
{
    /// <summary>從 <paramref name="index"/> 起略過所有空白與註解，回傳第一個實體字元的位置。</summary>
    public static int Skip(string text, int index, int end)
    {
        var position = index;

        while (position < end)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            if (StartsLineComment(text, position, end))
            {
                position = SkipLineComment(text, position, end);
                continue;
            }

            if (StartsBlockComment(text, position, end))
            {
                position = SkipBlockComment(text, position, end);
                continue;
            }

            break;
        }

        return position;
    }

    public static bool StartsLineComment(string text, int index, int end)
    {
        return text[index] == '-' && index + 1 < end && text[index + 1] == '-';
    }

    public static bool StartsBlockComment(string text, int index, int end)
    {
        return text[index] == '/' && index + 1 < end && text[index + 1] == '*';
    }

    /// <summary>跳過 <c>--</c> 註解，停在換行字元上（換行本身留給呼叫端當空白處理）。</summary>
    public static int SkipLineComment(string text, int index, int end)
    {
        index += 2;

        while (index < end && text[index] != '\r' && text[index] != '\n')
        {
            index++;
        }

        return index;
    }

    /// <summary>T-SQL 的區塊註解可以巢狀，因此要計算深度而不是找第一個結尾。</summary>
    public static int SkipBlockComment(string text, int index, int end)
    {
        var depth = 0;

        while (index < end)
        {
            if (index + 1 < end && text[index] == '/' && text[index + 1] == '*')
            {
                depth++;
                index += 2;
                continue;
            }

            if (index + 1 < end && text[index] == '*' && text[index + 1] == '/')
            {
                depth--;
                index += 2;

                if (depth == 0)
                {
                    return index;
                }

                continue;
            }

            index++;
        }

        return end;
    }
}
