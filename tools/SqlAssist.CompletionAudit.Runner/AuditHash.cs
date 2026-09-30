using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>快取鍵與版本指紋用的 SHA-256。</summary>
internal static class AuditHash
{
    public static string Of(string text) => Hex(Hash(Encoding.UTF8.GetBytes(text)));

    public static string OfFile(string path) => Hex(Hash(File.ReadAllBytes(path)));

    private static byte[] Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
    }

    private static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);

        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
