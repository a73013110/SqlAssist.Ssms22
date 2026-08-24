using System;
using System.Collections.Generic;
using System.Text;

namespace SqlAssist.Core.Parsing;

/// <summary>
/// T-SQL 詞法分析器。
/// </summary>
/// <remarks>
/// 只做詞法、不建語法樹。編輯中的敘述幾乎總是不完整，語法樹在這種輸入上不是失敗
/// 就是要靠錯誤復原猜測；詞法串流則永遠可得，而別名解析所需的資訊
/// （FROM／JOIN 之後的名稱與別名）在詞法層就足夠。
///
/// 空白與註解不會出現在輸出中，但每個詞法單元都帶原始位置，
/// 因此仍可對應回編輯器的位移。
/// </remarks>
public static class SqlTokenizer
{
    /// <summary>將整份文字切成詞法單元，略過空白與註解。</summary>
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        return Tokenize(sql, 0, sql?.Length ?? 0);
    }

    /// <summary>只切出 <paramref name="start"/> 到 <paramref name="end"/> 之間的詞法單元。</summary>
    public static IReadOnlyList<SqlToken> Tokenize(string sql, int start, int end)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (start < 0 || start > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end < start || end > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        var tokens = new List<SqlToken>();
        var index = start;

        while (index < end)
        {
            var current = sql[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < end && sql[index + 1] == '-')
            {
                index = SkipLineComment(sql, index, end);
                continue;
            }

            if (current == '/' && index + 1 < end && sql[index + 1] == '*')
            {
                index = SkipBlockComment(sql, index, end);
                continue;
            }

            if (current == '[')
            {
                tokens.Add(ReadDelimitedIdentifier(sql, index, end, ']', out index));
                continue;
            }

            if (current == '"')
            {
                tokens.Add(ReadDelimitedIdentifier(sql, index, end, '"', out index));
                continue;
            }

            if (current == '\'')
            {
                tokens.Add(ReadString(sql, index, index, end, out index));
                continue;
            }

            // N'...' 是 Unicode 字串，不是名為 N 的識別字。
            if ((current == 'N' || current == 'n') && index + 1 < end && sql[index + 1] == '\'')
            {
                tokens.Add(ReadString(sql, index, index + 1, end, out index));
                continue;
            }

            if (current == '@')
            {
                tokens.Add(ReadVariable(sql, index, end, out index));
                continue;
            }

            if (char.IsDigit(current) ||
                (current == '.' && index + 1 < end && char.IsDigit(sql[index + 1])))
            {
                tokens.Add(ReadNumber(sql, index, end, out index));
                continue;
            }

            if (IsIdentifierStart(current))
            {
                tokens.Add(ReadIdentifier(sql, index, end, out index));
                continue;
            }

            tokens.Add(ReadSymbol(sql, index, end, out index));
        }

        return tokens;
    }

    private static int SkipLineComment(string sql, int index, int end)
    {
        index += 2;

        while (index < end && sql[index] != '\r' && sql[index] != '\n')
        {
            index++;
        }

        return index;
    }

    /// <summary>T-SQL 的區塊註解可以巢狀，因此要計算深度而不是找第一個結尾。</summary>
    private static int SkipBlockComment(string sql, int index, int end)
    {
        var depth = 0;

        while (index < end)
        {
            if (index + 1 < end && sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
                continue;
            }

            if (index + 1 < end && sql[index] == '*' && sql[index + 1] == '/')
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

    /// <summary>讀出方括號或雙引號識別字，並還原重複結束字元的跳脫。</summary>
    private static SqlToken ReadDelimitedIdentifier(string sql, int start, int end, char closing, out int next)
    {
        var builder = new StringBuilder();
        var index = start + 1;
        var terminated = false;

        while (index < end)
        {
            if (sql[index] == closing)
            {
                if (index + 1 < end && sql[index + 1] == closing)
                {
                    builder.Append(closing);
                    index += 2;
                    continue;
                }

                index++;
                terminated = true;
                break;
            }

            builder.Append(sql[index]);
            index++;
        }

        // 未結束的識別字（正在輸入中）仍要回報，讓上層拿得到已輸入的前綴。
        _ = terminated;
        next = index;
        return new SqlToken(
            SqlTokenKind.Identifier,
            start,
            index - start,
            sql.Substring(start, index - start),
            builder.ToString(),
            isQuoted: true);
    }

    private static SqlToken ReadString(string sql, int start, int quoteIndex, int end, out int next)
    {
        var index = quoteIndex + 1;

        while (index < end)
        {
            if (sql[index] == '\'')
            {
                if (index + 1 < end && sql[index + 1] == '\'')
                {
                    index += 2;
                    continue;
                }

                index++;
                break;
            }

            index++;
        }

        next = index;
        var text = sql.Substring(start, index - start);
        return new SqlToken(SqlTokenKind.String, start, index - start, text, text, isQuoted: false);
    }

    private static SqlToken ReadVariable(string sql, int start, int end, out int next)
    {
        var index = start;

        while (index < end && sql[index] == '@')
        {
            index++;
        }

        while (index < end && IsIdentifierPart(sql[index]))
        {
            index++;
        }

        next = index;
        var text = sql.Substring(start, index - start);
        return new SqlToken(SqlTokenKind.Variable, start, index - start, text, text, isQuoted: false);
    }

    private static SqlToken ReadNumber(string sql, int start, int end, out int next)
    {
        var index = start;

        if (index + 1 < end && sql[index] == '0' && (sql[index + 1] == 'x' || sql[index + 1] == 'X'))
        {
            index += 2;

            while (index < end && Uri.IsHexDigit(sql[index]))
            {
                index++;
            }
        }
        else
        {
            while (index < end && (char.IsDigit(sql[index]) || sql[index] == '.'))
            {
                index++;
            }

            if (index < end && (sql[index] == 'e' || sql[index] == 'E'))
            {
                index++;

                if (index < end && (sql[index] == '+' || sql[index] == '-'))
                {
                    index++;
                }

                while (index < end && char.IsDigit(sql[index]))
                {
                    index++;
                }
            }
        }

        next = index;
        var text = sql.Substring(start, index - start);
        return new SqlToken(SqlTokenKind.Number, start, index - start, text, text, isQuoted: false);
    }

    private static SqlToken ReadIdentifier(string sql, int start, int end, out int next)
    {
        var index = start;

        while (index < end && IsIdentifierPart(sql[index]))
        {
            index++;
        }

        next = index;
        var text = sql.Substring(start, index - start);
        return new SqlToken(SqlTokenKind.Identifier, start, index - start, text, text, isQuoted: false);
    }

    private static SqlToken ReadSymbol(string sql, int start, int end, out int next)
    {
        // 先比對兩字元運算子，否則 <= 會被拆成 < 與 =。
        if (start + 1 < end)
        {
            var pair = sql.Substring(start, 2);

            if (pair == "<=" || pair == ">=" || pair == "<>" || pair == "!=" ||
                pair == "!<" || pair == "!>" || pair == "+=" || pair == "-=" ||
                pair == "*=" || pair == "/=" || pair == "%=" || pair == "|=" ||
                pair == "&=" || pair == "^=" || pair == "::")
            {
                next = start + 2;
                var kind = pair == "::" ? SqlTokenKind.Punctuation : SqlTokenKind.Operator;
                return new SqlToken(kind, start, 2, pair, pair, isQuoted: false);
            }
        }

        var single = sql[start].ToString();
        next = start + 1;
        var singleKind = IsPunctuationCharacter(sql[start])
            ? SqlTokenKind.Punctuation
            : SqlTokenKind.Operator;

        return new SqlToken(singleKind, start, 1, single, single, isQuoted: false);
    }

    private static bool IsPunctuationCharacter(char value)
    {
        return value == '.' || value == ',' || value == '(' || value == ')' ||
               value == ';' || value == ':';
    }

    /// <summary>
    /// 識別字的第一個字元。
    /// </summary>
    /// <remarks>
    /// <c>#</c> 是暫存表、<c>_</c> 是一般名稱，兩者都可以開頭。
    /// T-SQL 允許 Unicode 字母，因此用 <see cref="char.IsLetter(char)"/> 而非 A-Z 比對，
    /// 中文資料表名稱才不會被切碎。
    /// </remarks>
    private static bool IsIdentifierStart(char value)
    {
        return char.IsLetter(value) || value == '_' || value == '#';
    }

    private static bool IsIdentifierPart(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '#' || value == '$';
    }
}
