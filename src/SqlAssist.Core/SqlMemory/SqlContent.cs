using System;
using System.Security.Cryptography;
using System.Text;

namespace SqlAssist.Core.SqlMemory;

/// <summary>精確保留文字，不統一換行、大小寫或空白；只能在背景建立。</summary>
[Serializable]
public sealed class SqlContent
{
    private SqlContent(string contentHash, string sqlText)
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

    /// <summary>
    /// 這份 SQL 沒有內容：空字串，或全部都是空白字元。
    /// </summary>
    /// <remarks>
    /// 「什麼不值得記下來」只有這一份判斷：擷取（<see cref="SqlCapturePlanner"/>）、
    /// 「新增至收藏」與預覽的空狀態都問它。各寫一份的症狀是清單上留得下來的東西，
    /// 打開之後預覽說「這份 SQL 是空白內容」。
    ///
    /// 空白不正規化、也不參與去重——<see cref="Create"/> 仍然精確保留每一個 code unit。
    /// 這裡回答的只是「要不要記」，不是「這兩份算不算同一份」。
    ///
    /// 清理既有資料的那一份判斷寫在儲存層的 SQL 裡（<c>SqliteHistoryRows.Blank</c>），
    /// 兩邊不可能共用同一段程式碼；它只涵蓋 ASCII 空白與全形空白，而這一支照
    /// <see cref="char.IsWhiteSpace(char)"/> 走。代價是某些罕見空白字元的舊列要手動刪，
    /// 而新的擷取從一開始就不會產生。
    /// </remarks>
    public static bool IsBlank(string? sqlText) => string.IsNullOrWhiteSpace(sqlText);

    public static SqlContent Create(string sqlText)
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
        return new SqlContent(hex.ToString(), sqlText);
    }
}
