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

    /// <summary>
    /// 這一格是名稱格：接得了名稱、接不了值（不是運算式）。執行期拿它認既有名稱那一格（ALTER LOGIN 之後），
    /// 種類由尾巴的字決定。清單片語一律是 false。
    /// </summary>
    public bool TakesName { get; set; }

    /// <summary>宣告寫明了封閉（Closed = true）；探測的收尾不改它。不輸出。</summary>
    public bool DeclaredClosed { get; init; }

    /// <summary>宣告封閉時手寫的值：那一格只有這幾個字，之後的片語探測拿第一個代入名稱。不輸出。</summary>
    public IReadOnlyList<string> DeclaredValues { get; init; } = [];

    public bool EndsStatement { get; init; }

    /// <summary>這一格接得了名稱、值或括號：唯一接續的併項不跨過它。清單片語一律是 false。</summary>
    public bool TakesOperand { get; init; }

    /// <summary>
    /// 寫到這一格已是括號清單完整的一項：接得了逗號或右括號（端點的 ENCRYPTION = REQUIRED 之後可以寫完）。
    /// 唯一接續的併項不跨過它，否則選 REQUIRED 就被迫寫上可有可無的 ALGORITHM。不輸出。
    /// </summary>
    public bool EndsItem { get; init; }

    public List<string> Words { get; set; }

    /// <summary>附加片語：只加字，比對永遠是「可能」；寫進 AdditivePhrases，帶著尾巴。</summary>
    public bool Additive { get; set; }

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

    /// <summary>有片語的探測文字從 prefix 寫起：那一格或它之後已有別的宣告探過。</summary>
    public bool AnyProbeStartingWith(string prefix)
    {
        return _byProbe.Any(pair => pair.Value.Count > 0 && pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public string? FirstKeyWithProbe(string probe)
    {
        return _byProbe.TryGetValue(probe, out var slots) && slots.Count > 0 ? _keys[slots[0]] : null;
    }
}
