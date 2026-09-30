using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SqlAssist.CompletionAudit.Runner;

/// <summary>
/// 每段語料的結果快取：語料內容、判定程式與目錄都沒變就不重跑。
/// </summary>
/// <remarks>
/// 鍵是「判定版本＋資料庫與它的快照指紋＋語料全文」的雜湊，判定版本是判定組件本身的雜湊，
/// 所以改了 Core 或稽核程式，整份自然失效，不必記得清。每做完一段就排進待寫，定期附加到檔尾：
/// 中途被停（時間上限、Ctrl+C、當機）時，下一次從還沒做的那一段接著做。
/// 跑完整輪時只留這一輪用到的鍵，避免舊版本的結果越積越多。
/// </remarks>
internal sealed class AuditCache
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly ConcurrentDictionary<string, string> _lines = new(StringComparer.Ordinal);
    private readonly List<string> _pending = new();
    private readonly object _gate = new();
    private DateTime _flushedAt = DateTime.UtcNow;

    private AuditCache(string path)
    {
        _path = path;
    }

    public static AuditCache Load(string path, bool ignoreExisting)
    {
        var cache = new AuditCache(path);

        if (ignoreExisting || !File.Exists(path))
        {
            return cache;
        }

        foreach (var line in File.ReadLines(path))
        {
            try
            {
                if (line.Length > 0 && AuditJson.Parse(line).String("key") is { Length: > 0 } key)
                {
                    cache._lines[key] = line;
                }
            }
            catch (ArgumentException)
            {
                // 途中當機時最後一行可能只寫了一半；那一段下一次重跑。
            }
        }

        return cache;
    }

    public bool TryGet(string key, AuditFragment fragment, out AuditResult result)
    {
        if (!_lines.TryGetValue(key, out var line))
        {
            result = null!;
            return false;
        }

        var entry = AuditJson.Parse(line);
        var tally = new AuditTally();

        foreach (var pair in entry.Object("audited"))
        {
            tally.Audit((AuditTokenClass)Enum.Parse(typeof(AuditTokenClass), pair.Key), Convert.ToInt32(pair.Value));
        }

        foreach (var pair in entry.Object("excluded"))
        {
            tally.Exclude((AuditExclusion)Enum.Parse(typeof(AuditExclusion), pair.Key), Convert.ToInt32(pair.Value));
        }

        var misses = entry.Array("misses").OfType<Dictionary<string, object>>().Select(miss => MissRecord.Read(miss).ToMiss(fragment)).ToArray();
        tally.Miss(misses.Length);
        result = new AuditResult(misses, tally);
        return true;
    }

    public void Add(string key, AuditResult result)
    {
        var line = AuditJson.Serialize(new Dictionary<string, object>
        {
            ["key"] = key,
            ["audited"] = result.Tally.Audited.ToDictionary(pair => pair.Key.ToString(), pair => (object)pair.Value),
            ["excluded"] = result.Tally.Excluded.ToDictionary(pair => pair.Key.ToString(), pair => (object)pair.Value),
            ["misses"] = result.Misses.Select(miss => MissRecord.From(miss).Write()).ToArray(),
        });

        _lines[key] = line;

        lock (_gate)
        {
            _pending.Add(line);

            if (DateTime.UtcNow - _flushedAt >= FlushInterval)
            {
                FlushLocked();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    /// <summary>整輪做完時重寫，只留 <paramref name="keys"/>。</summary>
    public void Compact(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            _pending.Clear();
            var temporary = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(
                temporary,
                keys.Distinct(StringComparer.Ordinal).Where(_lines.ContainsKey).Select(key => _lines[key]),
                new UTF8Encoding(false));

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            File.Move(temporary, _path);
            _flushedAt = DateTime.UtcNow;
        }
    }

    private void FlushLocked()
    {
        if (_pending.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllLines(_path, _pending, new UTF8Encoding(false));
            _pending.Clear();
        }

        _flushedAt = DateTime.UtcNow;
    }
}

/// <summary>一筆漏的可序列化形狀；快取與原始紀錄共用。</summary>
internal sealed class MissRecord
{
    public int Offset { get; private set; }

    public string Word { get; private set; } = string.Empty;

    public AuditTokenClass Class { get; private set; }

    public AuditMissKind Kind { get; private set; }

    public string Signature { get; private set; } = string.Empty;

    public string Example { get; private set; } = string.Empty;

    public string Masked { get; private set; } = string.Empty;

    public int Length { get; private set; }

    public bool? Ssms { get; private set; }

    public static MissRecord From(AuditMiss miss) => new()
    {
        Offset = miss.Offset,
        Word = miss.Word,
        Class = miss.TokenClass,
        Kind = miss.Kind,
        Signature = miss.Signature.Text,
        Example = miss.Example,
        Masked = miss.MaskedExample,
        Length = miss.ExampleLength,
        Ssms = miss.SsmsLists,
    };

    public static MissRecord Read(Dictionary<string, object> node) => new()
    {
        Offset = node.Int("offset"),
        Word = node.RequiredString("word"),
        Class = (AuditTokenClass)Enum.Parse(typeof(AuditTokenClass), node.RequiredString("class")),
        Kind = (AuditMissKind)Enum.Parse(typeof(AuditMissKind), node.RequiredString("kind")),
        Signature = node.RequiredString("signature"),
        Example = node.RequiredString("example"),
        Masked = node.RequiredString("masked"),
        Length = node.Int("length"),
        Ssms = node.NullableBool("ssms"),
    };

    public Dictionary<string, object?> Write() => new()
    {
        ["offset"] = Offset,
        ["word"] = Word,
        ["class"] = Class.ToString(),
        ["kind"] = Kind.ToString(),
        ["signature"] = Signature,
        ["example"] = Example,
        ["masked"] = Masked,
        ["length"] = Length,
        ["ssms"] = Ssms,
    };

    public AuditMiss ToMiss(AuditFragment fragment) =>
        new(fragment, Offset, Word, Class, Kind, new AuditSignature(Signature), Example, Masked, Length) { SsmsLists = Ssms };
}
