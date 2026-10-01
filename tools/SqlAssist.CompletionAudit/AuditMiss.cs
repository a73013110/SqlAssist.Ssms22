using System;
using System.Collections.Generic;

namespace SqlAssist.CompletionAudit;

/// <summary>一個列不出來的詞：在哪一段、哪個位置、哪一種漏，以及它屬於哪一群。</summary>
public sealed class AuditMiss
{
    public AuditMiss(
        AuditFragment fragment,
        int offset,
        string word,
        AuditTokenClass tokenClass,
        AuditMissKind kind,
        AuditSignature signature,
        string example,
        string maskedExample,
        int exampleLength)
    {
        Fragment = fragment ?? throw new ArgumentNullException(nameof(fragment));
        Offset = offset;
        Word = word;
        TokenClass = tokenClass;
        Kind = kind;
        Signature = signature;
        Example = example;
        MaskedExample = maskedExample;
        ExampleLength = exampleLength;
    }

    public AuditFragment Fragment { get; }

    /// <summary>那個詞在整段語料裡的起點。</summary>
    public int Offset { get; }

    /// <summary>作者寫的詞，原文。</summary>
    public string Word { get; }

    public AuditTokenClass TokenClass { get; }

    public AuditMissKind Kind { get; }

    public AuditSignature Signature { get; }

    /// <summary>那一句從開頭到游標、標上游標與答案的原文，最多三行。</summary>
    public string Example { get; }

    /// <summary>同一段，名稱換成代號、常值遮掉。</summary>
    public string MaskedExample { get; }

    /// <summary>那一句從開頭到游標的長度；挑一群的代表範例時取最短的。</summary>
    public int ExampleLength { get; }

    /// <summary>SSMS 的 Resolver 在同一個位置列不列得出這個詞；沒問過是 null。</summary>
    public bool? SsmsLists { get; set; }
}

/// <summary>一段語料的結果：漏在哪裡，以及稽核了多少、排除了多少。</summary>
public sealed class AuditResult
{
    public AuditResult(IReadOnlyList<AuditMiss> misses, AuditTally tally)
    {
        Misses = misses ?? throw new ArgumentNullException(nameof(misses));
        Tally = tally ?? throw new ArgumentNullException(nameof(tally));
    }

    public IReadOnlyList<AuditMiss> Misses { get; }

    public AuditTally Tally { get; }
}

/// <summary>稽核與排除的計數。</summary>
public sealed class AuditTally
{
    private readonly Dictionary<AuditTokenClass, int> _audited = new();
    private readonly Dictionary<AuditExclusion, int> _excluded = new();

    public IReadOnlyDictionary<AuditTokenClass, int> Audited => _audited;

    public IReadOnlyDictionary<AuditExclusion, int> Excluded => _excluded;

    public int Misses { get; private set; }

    /// <summary>剖析不過的句數；那些詞元記在 <see cref="AuditExclusion.Unparsed"/>。</summary>
    public int UnparsedStatements { get; private set; }

    public void Audit(AuditTokenClass tokenClass, int count = 1) => _audited[tokenClass] = Get(_audited, tokenClass) + count;

    public void Exclude(AuditExclusion exclusion, int count = 1) => _excluded[exclusion] = Get(_excluded, exclusion) + count;

    public void Miss(int count = 1) => Misses += count;

    public void Unparse(int statements) => UnparsedStatements += statements;

    public void Add(AuditTally other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        foreach (var pair in other._audited)
        {
            _audited[pair.Key] = Get(_audited, pair.Key) + pair.Value;
        }

        foreach (var pair in other._excluded)
        {
            _excluded[pair.Key] = Get(_excluded, pair.Key) + pair.Value;
        }

        Misses += other.Misses;
        UnparsedStatements += other.UnparsedStatements;
    }

    private static int Get<TKey>(Dictionary<TKey, int> counts, TKey key) =>
        counts.TryGetValue(key, out var count) ? count : 0;
}
