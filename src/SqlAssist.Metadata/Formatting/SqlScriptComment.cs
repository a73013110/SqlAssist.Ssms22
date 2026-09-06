using System.Text;

namespace SqlAssist.Metadata.Formatting;

/// <summary>將外部文字完整留在行註解內，不讓名稱或運算式中的換行變成可執行 SQL。</summary>
internal static class SqlScriptComment
{
    public static void AppendLine(StringBuilder builder, string text, string newLine)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\r' && text[index] != '\n')
            {
                continue;
            }

            builder.Append("-- ").Append(text, start, index - start).Append(newLine);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }
            start = index + 1;
        }

        builder.Append("-- ").Append(text, start, text.Length - start).Append(newLine);
    }
}
