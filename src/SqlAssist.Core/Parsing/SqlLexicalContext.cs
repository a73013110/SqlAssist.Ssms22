
namespace SqlAssist.Core.Parsing;

public static class SqlLexicalContext
{
    /// <summary>指定位置是否位於一般程式碼中（不在字串、註解或引號識別字內）。</summary>
    public static bool IsCode(string sql, int position)
    {
        return GetState(sql, position) == SqlLexicalState.Code;
    }

    /// <summary>不必先把整份文字複製成字串的多載。</summary>
    public static bool IsCode(ISqlTextSource sql, int position)
    {
        return GetState(sql, position) == SqlLexicalState.Code;
    }

    /// <summary>
    /// 判斷指定位置的語彙狀態。
    /// </summary>
    /// <remarks>
    /// 方括號與雙引號識別字要與字串、註解分開判斷：它們雖然不是「一般程式碼」，
    /// 但裡面放的是物件名稱，滑鼠停留提示與物件解析都應該處理。
    /// </remarks>
    public static SqlLexicalState GetState(string sql, int position)
    {
        if (sql is null)
        {
            throw new System.ArgumentNullException(nameof(sql));
        }

        return GetState(new SqlStringText(sql), position);
    }

    /// <summary>
    /// 判斷指定位置的語彙狀態，直接讀取文字來源。
    /// </summary>
    /// <remarks>
    /// 這個判斷必然要從頭掃到 <paramref name="position"/>，但沒有理由為此
    /// 先複製一份文字；編輯器的快照可以直接餵進來。
    /// </remarks>
    public static SqlLexicalState GetState(ISqlTextSource sql, int position)
    {
        if (sql is null)
        {
            throw new System.ArgumentNullException(nameof(sql));
        }

        if (position < 0 || position > sql.Length)
        {
            throw new System.ArgumentOutOfRangeException(nameof(position));
        }

        var state = SqlLexicalState.Code;

        for (var index = 0; index < position; index++)
        {
            var current = sql[index];
            var next = index + 1 < position ? sql[index + 1] : '\0';

            switch (state)
            {
                case SqlLexicalState.Code:
                    if (current == '-' && next == '-')
                    {
                        state = SqlLexicalState.LineComment;
                        index++;
                    }
                    else if (current == '/' && next == '*')
                    {
                        state = SqlLexicalState.BlockComment;
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = SqlLexicalState.String;
                    }
                    else if (current == '"')
                    {
                        state = SqlLexicalState.QuotedIdentifier;
                    }
                    else if (current == '[')
                    {
                        state = SqlLexicalState.BracketedIdentifier;
                    }

                    break;

                case SqlLexicalState.LineComment:
                    if (current == '\r' || current == '\n')
                    {
                        state = SqlLexicalState.Code;
                    }

                    break;

                case SqlLexicalState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        state = SqlLexicalState.Code;
                        index++;
                    }

                    break;

                case SqlLexicalState.String:
                    if (current == '\'' && next == '\'')
                    {
                        index++; // SQL 字串內的兩個單引號代表跳脫字元。
                    }
                    else if (current == '\'')
                    {
                        state = SqlLexicalState.Code;
                    }

                    break;

                case SqlLexicalState.QuotedIdentifier:
                    if (current == '"' && next == '"')
                    {
                        index++; // 雙引號識別字內的重複雙引號不會結束識別字。
                    }
                    else if (current == '"')
                    {
                        state = SqlLexicalState.Code;
                    }

                    break;

                case SqlLexicalState.BracketedIdentifier:
                    if (current == ']' && next == ']')
                    {
                        index++; // 方括號識別字使用 ]] 表示右方括號。
                    }
                    else if (current == ']')
                    {
                        state = SqlLexicalState.Code;
                    }

                    break;
            }
        }

        return state;
    }
}
