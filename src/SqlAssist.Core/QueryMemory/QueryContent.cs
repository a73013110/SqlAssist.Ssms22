using System;
using System.Security.Cryptography;
using System.Text;

namespace SqlAssist.Core.QueryMemory;

/// <summary>精確保留文字，不統一換行、大小寫或空白；只能在背景建立。</summary>
[Serializable]
public sealed class QueryContent
{
    private QueryContent(string contentHash, string sqlText)
    {
        ContentHash = contentHash;
        ContentId = "sha256-utf16le:" + contentHash;
        SqlText = sqlText;
    }

    // 使用有演算法前綴的內容位址，未來更換儲存壓縮方式不影響 Revision。
    public string ContentId { get; }
    public string ContentHash { get; }
    public string SqlText { get; }
    public int Length => SqlText.Length;

    public static QueryContent Create(string sqlText)
    {
        if (sqlText == null) throw new ArgumentNullException(nameof(sqlText));
        using var sha = SHA256.Create();
        var buffer = new byte[8192];
        for (var start = 0; start < sqlText.Length;)
        {
            var count = Math.Min(buffer.Length / 2, sqlText.Length - start);
            for (var i = 0; i < count; i++)
            {
                // 逐一保存 UTF-16 code unit，避免替代字元讓不完整 surrogate 與其他 SQL 碰撞。
                var value = sqlText[start + i];
                buffer[i * 2] = (byte)value;
                buffer[i * 2 + 1] = (byte)(value >> 8);
            }
            sha.TransformBlock(buffer, 0, count * 2, buffer, 0);
            start += count;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = sha.Hash ?? throw new InvalidOperationException("無法取得 SQL 內容雜湊。");
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var value in hash) hex.Append(value.ToString("x2"));
        return new QueryContent(hex.ToString(), sqlText);
    }
}
