using System;

namespace SqlAssist.Metadata;

public static class SqlIdentifier
{
    /// <summary>以方括號括住識別字，內部的右方括號會被跳脫成 <c>]]</c>。</summary>
    public static string Quote(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>
    /// 判斷識別字是否可以不加括號直接書寫：開頭為字母或底線，
    /// 其餘為字母、數字、底線、井號或小老鼠，且不是保留字形式。
    /// </summary>
    public static bool IsRegular(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            var value = name[index];

            if (!char.IsLetterOrDigit(value) && value != '_' && value != '#' && value != '@' && value != '$')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>只有在必要時才加上方括號。</summary>
    public static string QuoteIfNeeded(string name)
    {
        return IsRegular(name) ? name : Quote(name);
    }
}
