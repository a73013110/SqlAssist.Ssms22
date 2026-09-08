using System;
using System.Text;

namespace SqlAssist.Core.Snippets;

public static class SqlSnippetIdentity
{
    public static string NewCustomId() => "user." + Guid.NewGuid().ToString("N");

    /// <summary>手改檔案沒寫 <c>id</c> 時，從捷徑推出識別碼。</summary>
    /// <remarks>
    /// 不能用 <see cref="NewCustomId"/>：同一份檔案每次載入都會得到新的識別碼，
    /// 包夾的最近使用紀錄與管理介面的選取都跟著失憶。
    /// </remarks>
    public static string CreateIdFromShortcut(string shortcut)
    {
        var builder = new StringBuilder("user.");

        foreach (var character in (shortcut ?? string.Empty).ToLowerInvariant())
        {
            builder.Append(IsPart(character) ? character : '_');
        }

        return builder.Length == "user.".Length
            ? "user.snippet"
            : builder.ToString();
    }

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IsStart(id![0]))
        {
            return false;
        }

        for (var index = 1; index < id.Length; index++)
        {
            if (!IsPart(id[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStart(char value) => value is >= 'a' and <= 'z';

    private static bool IsPart(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}
