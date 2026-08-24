using System;

namespace SqlAssist.Core.Matching;

/// <summary>
/// 候選字串中被比對命中的一段連續字元，供建議清單高亮使用。
/// </summary>
public readonly struct MatchSpan : IEquatable<MatchSpan>
{
    public MatchSpan(int start, int length)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Start = start;
        Length = length;
    }

    public int Start { get; }

    public int Length { get; }

    public int End => Start + Length;

    public bool Equals(MatchSpan other)
    {
        return Start == other.Start && Length == other.Length;
    }

    public override bool Equals(object? obj)
    {
        return obj is MatchSpan other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (Start * 397) ^ Length;
        }
    }

    public override string ToString()
    {
        return $"[{Start}..{End})";
    }
}
