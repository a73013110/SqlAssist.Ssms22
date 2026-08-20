namespace SqlAssist.Core;

public static class SqlLexicalContext
{
    public static bool IsCode(string sql, int position)
    {
        var state = LexicalState.Code;

        for (var index = 0; index < position; index++)
        {
            var current = sql[index];
            var next = index + 1 < position ? sql[index + 1] : '\0';

            switch (state)
            {
                case LexicalState.Code:
                    if (current == '-' && next == '-')
                    {
                        state = LexicalState.LineComment;
                        index++;
                    }
                    else if (current == '/' && next == '*')
                    {
                        state = LexicalState.BlockComment;
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = LexicalState.String;
                    }
                    else if (current == '"')
                    {
                        state = LexicalState.QuotedIdentifier;
                    }
                    else if (current == '[')
                    {
                        state = LexicalState.BracketedIdentifier;
                    }

                    break;

                case LexicalState.LineComment:
                    if (current == '\r' || current == '\n')
                    {
                        state = LexicalState.Code;
                    }

                    break;

                case LexicalState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        state = LexicalState.Code;
                        index++;
                    }

                    break;

                case LexicalState.String:
                    if (current == '\'' && next == '\'')
                    {
                        index++; // SQL 字串內的兩個單引號代表跳脫字元。
                    }
                    else if (current == '\'')
                    {
                        state = LexicalState.Code;
                    }

                    break;

                case LexicalState.QuotedIdentifier:
                    if (current == '"' && next == '"')
                    {
                        index++; // 雙引號識別字內的重複雙引號不會結束識別字。
                    }
                    else if (current == '"')
                    {
                        state = LexicalState.Code;
                    }

                    break;

                case LexicalState.BracketedIdentifier:
                    if (current == ']' && next == ']')
                    {
                        index++; // 方括號識別字使用 ]] 表示右方括號。
                    }
                    else if (current == ']')
                    {
                        state = LexicalState.Code;
                    }

                    break;
            }
        }

        return state == LexicalState.Code;
    }

    private enum LexicalState
    {
        Code,
        LineComment,
        BlockComment,
        String,
        QuotedIdentifier,
        BracketedIdentifier
    }
}
