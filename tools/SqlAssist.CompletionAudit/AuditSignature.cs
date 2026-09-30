using System;
using System.Security.Cryptography;
using System.Text;

namespace SqlAssist.CompletionAudit;

/// <summary>
/// 一個漏的位置簽章：前兩個詞元的形狀、答案（字原樣、名稱只留種類）與漏的樣子。
/// </summary>
/// <remarks>
/// 只看文字的形狀，不看產品自己的位置分析：分析器改版時簽章不能跟著變，否則跨夜保留的分類
/// （修好、忽略）認不回同一群，退化也就看不出來。名稱換成種類，同一種寫法在不同資料表上是同一群。
/// </remarks>
public readonly struct AuditSignature
{
    public AuditSignature(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Id = Hash(text);
    }

    /// <summary>可讀的簽章，例如 <c>BY ‹name› ⎵ASC [Absent]</c>。</summary>
    public string Text { get; }

    /// <summary>穩定識別：簽章的 SHA-1 前八個十六進位字元。</summary>
    public string Id { get; }

    public override string ToString() => $"{Id} {Text}";

    private static string Hash(string text)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(8);

        for (var index = 0; index < 4; index++)
        {
            builder.Append(bytes[index].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
