using System;
using System.Text;

namespace SqlAssist.Core.Snippets;

public static class SqlSnippetIdentity
{
    public static string NewCustomId() => "user." + Guid.NewGuid().ToString("N");

    /// <summary>v1 遷移必須冪等，所以不能使用每次都不同的 Guid。</summary>
    public static string CreateMigratedId(string shortcut)
    {
        var builder = new StringBuilder("user.v1.");

        foreach (var character in (shortcut ?? string.Empty).ToLowerInvariant())
        {
            builder.Append(IsPart(character) ? character : '_');
        }

        return builder.Length == "user.v1.".Length
            ? "user.v1.snippet"
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
