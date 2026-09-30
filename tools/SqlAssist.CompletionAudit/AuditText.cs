using System;
using SqlAssist.Core.Parsing;

namespace SqlAssist.CompletionAudit;

/// <summary>答案與清單項目怎麼比：不分大小寫、去方括號與雙引號、多段名稱比最後一段。</summary>
public static class AuditText
{
    /// <summary>比對用的名稱：去掉方括號或雙引號、轉成大寫。</summary>
    public static string Normalize(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return SqlIdentifier.Unquote(name.Trim()).ToUpperInvariant();
    }

    /// <summary>建議項顯示文字的最後一段（<c>dbo.Lib_Reader</c> → <c>LIB_READER</c>）。</summary>
    public static string LastPart(string displayText)
    {
        if (displayText is null)
        {
            throw new ArgumentNullException(nameof(displayText));
        }

        var dot = displayText.LastIndexOf('.');
        return Normalize(dot >= 0 && dot < displayText.Length - 1 ? displayText.Substring(dot + 1) : displayText);
    }

    /// <summary>
    /// 建議項的顯示文字以這幾個字開頭；<c>INDEX(</c>、<c>varchar(n)</c> 的括號不算字。
    /// </summary>
    public static bool StartsWithWords(string displayText, string[] words)
    {
        if (displayText is null)
        {
            throw new ArgumentNullException(nameof(displayText));
        }

        var parts = displayText.Split(new[] { ' ', '(' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < words.Length)
        {
            return false;
        }

        for (var index = 0; index < words.Length; index++)
        {
            if (!string.Equals(parts[index], words[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
