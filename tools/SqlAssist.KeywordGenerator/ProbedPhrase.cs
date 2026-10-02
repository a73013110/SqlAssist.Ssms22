using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.KeywordGenerator;

/// <summary>探測出來的片語，產物 ClausePhrases 的一列；一般片語、清單片語與併過位置的都是這一種。</summary>
public sealed class ProbedPhrase
{
    public ProbedPhrase(string pattern, string after, string probe, IEnumerable<string> words)
    {
        Pattern = pattern;
        After = [after];
        Probe = probe;
        Words = words.ToList();
    }

    public string Pattern { get; }

    /// <summary>前一格的位置。探測時一個，同結果併位置之後是幾個。</summary>
    public List<string> After { get; }

    public string Probe { get; }

    public bool Closed { get; set; }

    public bool TakesVariable { get; set; }

    public bool EndsStatement { get; init; }

    /// <summary>這一格接得了名稱、值或括號：唯一接續的併項不跨過它。清單片語一律是 false。</summary>
    public bool TakesOperand { get; init; }

    public List<string> Words { get; set; }

    /// <summary>探測時的鍵：同一條尾巴在不同位置是不同的片語。表裡的鍵不分大小寫。</summary>
    internal static string Key(string after, string pattern)
    {
        return after + "\t" + pattern;
    }
}

/// <summary>
/// 探測中的片語表：照加入的順序（產物的順序），同一個鍵再寫一次是原地覆蓋。
/// 另以探測文字建索引：「同一段探測文字的片語」問得很頻繁，逐一掃描整張表要好幾秒。
/// </summary>
internal sealed class PhraseTable
{
    private readonly Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _keys = [];
    private readonly List<ProbedPhrase> _phrases = [];
    private readonly Dictionary<string, List<int>> _byProbe = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProbedPhrase> Values => _phrases;

    public IReadOnlyList<string> Keys => _keys;

    public ProbedPhrase this[string key] => _phrases[_slots[key]];

    public bool Contains(string key)
    {
        return _slots.ContainsKey(key);
    }

    public bool TryGet(string key, out ProbedPhrase phrase)
    {
        if (_slots.TryGetValue(key, out var slot))
        {
            phrase = _phrases[slot];
            return true;
        }

        phrase = null!;
        return false;
    }

    public void Set(string key, ProbedPhrase phrase)
    {
        if (_slots.TryGetValue(key, out var slot))
        {
            var old = _phrases[slot];
            _phrases[slot] = phrase;

            if (string.Equals(old.Probe, phrase.Probe, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _byProbe[old.Probe].Remove(slot);
        }
        else
        {
            slot = _phrases.Count;
            _slots.Add(key, slot);
            _keys.Add(key);
            _phrases.Add(phrase);
        }

        if (!_byProbe.TryGetValue(phrase.Probe, out var slots))
        {
            _byProbe.Add(phrase.Probe, slots = []);
        }

        // 覆蓋不改位置，索引裡的順序仍要照表的順序。
        var index = slots.BinarySearch(slot);
        slots.Insert(~index, slot);
    }

    /// <summary>探測文字相同的片語，照表的順序。</summary>
    public IEnumerable<ProbedPhrase> WithProbe(string probe)
    {
        return _byProbe.TryGetValue(probe, out var slots) ? slots.Select(slot => _phrases[slot]) : [];
    }

    public string? FirstKeyWithProbe(string probe)
    {
        return _byProbe.TryGetValue(probe, out var slots) && slots.Count > 0 ? _keys[slots[0]] : null;
    }
}
