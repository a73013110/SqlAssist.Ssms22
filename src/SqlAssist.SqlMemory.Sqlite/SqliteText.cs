using System;
using System.IO;

namespace SqlAssist.SqlMemory.Sqlite;

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

    /// <summary>預覽欄位保留的字元數；空白列的清理條件靠它判斷整份內容都在預覽裡。</summary>
    public const int PreviewLength = 240;

    public static string Preview(string text)
    {
        var length = Math.Min(text.Length, PreviewLength);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text.Substring(0, length).Replace('\0', ' ');
    }
}
