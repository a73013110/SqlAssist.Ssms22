using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

public enum BlockKind { Block, Try, Catch, Case, Parenthesis, Bracket, String }

/// <summary>半開區間；複合端點分成詞元，註解與空白不算關鍵字。</summary>
public readonly struct BlockSpan
{
    public BlockSpan(int start, int length) { Start = start; Length = length; }
    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
    public bool Contains(int position) => position >= Start && position < End;
}

/// <summary>不依賴編輯器的不可變配對，供高亮、摺疊與未來導覽共用。</summary>
public sealed class BlockPair
{
    internal BlockPair(BlockKind kind, BlockSpan[] opening, BlockSpan[] closing)
    {
        Kind = kind;
        Opening = System.Array.AsReadOnly(opening);
        Closing = System.Array.AsReadOnly(closing);
        Span = new BlockSpan(opening[0].Start, closing[closing.Length - 1].End - opening[0].Start);
    }

    public BlockKind Kind { get; }
    public IReadOnlyList<BlockSpan> Opening { get; }
    public IReadOnlyList<BlockSpan> Closing { get; }
    public BlockSpan Span { get; }
}
