using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 以本機金鑰把名稱換成穩定代號：同一個名稱每一夜都是同一個代號，沒有金鑰就換不回去。
/// </summary>
/// <remarks>
/// 代號以種類開頭（T 物件、C 欄位、S 結構描述、D 資料庫、A 指令碼取的名稱、N 不明），
/// 變數保留小老鼠、暫存資料表保留井號，讀報告時看得出那一格在接什麼。
/// 金鑰只在第一次產生，存在版控外；對照表只寫進原始紀錄，報告裡只有代號。
/// </remarks>
internal sealed class SaltedMask : IAuditMask
{
    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.Ordinal);

    private SaltedMask(byte[] key)
    {
        _key = key;
    }

    /// <summary>這一輪換過的代號與原名。</summary>
    public IEnumerable<KeyValuePair<string, string>> Codes => _codes;

    public static SaltedMask Load(string keyPath)
    {
        if (!File.Exists(keyPath))
        {
            var key = new byte[32];

            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(key);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllBytes(keyPath, key);
        }

        return new SaltedMask(File.ReadAllBytes(keyPath));
    }

    public string Mask(string name, AuditTokenClass? tokenClass)
    {
        var sigil = name.StartsWith("@", StringComparison.Ordinal) ? "@"
            : name.StartsWith("#", StringComparison.Ordinal) ? "#"
            : string.Empty;
        var prefix = tokenClass switch
        {
            AuditTokenClass.Object => "T",
            AuditTokenClass.Column => "C",
            AuditTokenClass.Schema => "S",
            AuditTokenClass.Database => "D",
            AuditTokenClass.ScriptName => "A",
            _ => "N",
        };

        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(AuditText.Normalize(name)));
        var code = $"{sigil}{prefix}_{hash[0]:x2}{hash[1]:x2}{hash[2]:x2}";
        _codes.TryAdd(code, name);
        return code;
    }
}
