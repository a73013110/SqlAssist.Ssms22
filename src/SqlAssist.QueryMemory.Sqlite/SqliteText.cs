using System;
using System.IO;

namespace SqlAssist.QueryMemory.Sqlite;

internal static class SqliteText
{
    // SQLite TEXT 的 UTF-8 轉換會改寫未配對 surrogate；Recovery 本體改以 UTF-16LE BLOB 保存。
    public static byte[] Encode(string text)
    {
        var bytes = new byte[checked(text.Length * 2)];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i * 2] = (byte)text[i];
            bytes[i * 2 + 1] = (byte)(text[i] >> 8);
        }
        return bytes;
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length % 2 != 0) throw new InvalidDataException("SQL 內容位元組長度不正確。");
        var chars = new char[bytes.Length / 2];
        for (var i = 0; i < chars.Length; i++) chars[i] = (char)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return new string(chars);
    }

    public static string Preview(string text)
    {
        var length = Math.Min(text.Length, 240);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text.Substring(0, length).Replace('\0', ' ');
    }
}

/// <summary>字面、區分大小寫的 KMP 搜尋；每次查詢只編譯一次，不為每列建立完整 SQL 字串。</summary>
internal sealed class SqliteTextMatcher
{
    private readonly string _needle;
    private readonly int[] _prefix;

    public SqliteTextMatcher(string needle)
    {
        _needle = needle;
        _prefix = new int[needle.Length];
        for (int i = 1, j = 0; i < needle.Length; i++)
        {
            while (j > 0 && needle[i] != needle[j]) j = _prefix[j - 1];
            if (needle[i] == needle[j]) j++;
            _prefix[i] = j;
        }
    }

    public bool Matches(byte[] bytes)
    {
        if (_needle.Length == 0) return true;
        for (int i = 0, j = 0; i + 1 < bytes.Length; i += 2)
        {
            var value = (char)(bytes[i] | (bytes[i + 1] << 8));
            while (j > 0 && value != _needle[j]) j = _prefix[j - 1];
            if (value == _needle[j]) j++;
            if (j == _needle.Length) return true;
        }
        return false;
    }
}
