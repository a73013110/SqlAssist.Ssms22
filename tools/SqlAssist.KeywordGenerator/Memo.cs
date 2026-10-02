using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SqlAssist.KeywordGenerator;

/// <summary>以文字為鍵的記憶化：先找這一次用過的，再找快取讀進來的，都沒有才算。</summary>
internal sealed class Memo<T>
{
    private readonly ConcurrentDictionary<string, T> _loaded = new ConcurrentDictionary<string, T>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, T> _used = new ConcurrentDictionary<string, T>(StringComparer.Ordinal);
    private int _hits;
    private int _misses;

    public int Hits => Volatile.Read(ref _hits);

    public int Misses => Volatile.Read(ref _misses);

    public T Get(string key, Func<string, T> compute)
    {
        if (_used.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_loaded.TryGetValue(key, out value))
        {
            Interlocked.Increment(ref _hits);
        }
        else
        {
            value = compute(key);
            Interlocked.Increment(ref _misses);
        }

        _used[key] = value;
        return value;
    }

    public void Read(BinaryReader reader, Func<BinaryReader, T> read)
    {
        var count = reader.ReadInt32();

        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            _loaded[key] = read(reader);
        }
    }

    public void ForgetLoaded()
    {
        _loaded.Clear();
    }

    public void Write(BinaryWriter writer, bool prune, Action<BinaryWriter, T> write)
    {
        var entries = new List<KeyValuePair<string, T>>(_used);

        if (!prune)
        {
            foreach (var entry in _loaded)
            {
                if (!_used.ContainsKey(entry.Key))
                {
                    entries.Add(entry);
                }
            }
        }

        writer.Write(entries.Count);

        foreach (var entry in entries)
        {
            writer.Write(entry.Key);
            write(writer, entry.Value);
        }
    }
}
