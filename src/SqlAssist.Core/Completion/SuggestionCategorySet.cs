using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Completion;

/// <summary>
/// 一組 <see cref="SuggestionCategory"/>，以位元旗標存放。
/// </summary>
/// <remarks>
/// 篩選在按鍵路徑上，每一次按鍵都要為每一筆命中記下分類；用位元旗標是為了那一步
/// 只做一次 OR、不配置記憶體。
/// </remarks>
public readonly struct SuggestionCategorySet : IEquatable<SuggestionCategorySet>
{
    private readonly int _bits;

    private SuggestionCategorySet(int bits) => _bits = bits;

    public static SuggestionCategorySet Empty => default;

    public bool IsEmpty => _bits == 0;

    public int Count
    {
        get
        {
            var count = 0;
            for (var bits = _bits; bits != 0; bits &= bits - 1)
            {
                count++;
            }

            return count;
        }
    }

    public static SuggestionCategorySet Of(params SuggestionCategory[] categories)
    {
        var set = Empty;
        foreach (var category in categories)
        {
            set = set.With(category);
        }

        return set;
    }

    public bool Contains(SuggestionCategory category) => (_bits & Bit(category)) != 0;

    public SuggestionCategorySet With(SuggestionCategory category) => new(_bits | Bit(category));

    public SuggestionCategorySet Intersect(SuggestionCategorySet other) => new(_bits & other._bits);

    /// <summary>依 <see cref="SuggestionCategory"/> 的宣告順序列出。</summary>
    public IEnumerable<SuggestionCategory> InOrder()
    {
        foreach (SuggestionCategory category in Enum.GetValues(typeof(SuggestionCategory)))
        {
            if (Contains(category))
            {
                yield return category;
            }
        }
    }

    public bool Equals(SuggestionCategorySet other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is SuggestionCategorySet other && Equals(other);

    public override int GetHashCode() => _bits;

    public static bool operator ==(SuggestionCategorySet left, SuggestionCategorySet right) => left.Equals(right);

    public static bool operator !=(SuggestionCategorySet left, SuggestionCategorySet right) => !left.Equals(right);

    public override string ToString() => string.Join(", ", InOrder());

    private static int Bit(SuggestionCategory category) => 1 << (int)category;
}
