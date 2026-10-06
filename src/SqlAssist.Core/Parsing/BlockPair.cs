using System.Collections.Generic;

namespace SqlAssist.Core.Parsing;

public enum BlockKind { Block, Try, Catch, Case, Parenthesis, Bracket, String }

/// <summary>不依賴編輯器的不可變配對，供高亮、摺疊與未來導覽共用；複合端點分成詞元，註解與空白不算關鍵字。</summary>
public sealed class BlockPair
{
    internal BlockPair(BlockKind kind, SqlTextSpan[] opening, SqlTextSpan[] closing)
    {
        Kind = kind;
        Opening = System.Array.AsReadOnly(opening);
        Closing = System.Array.AsReadOnly(closing);
        Span = new SqlTextSpan(opening[0].Start, closing[closing.Length - 1].End - opening[0].Start);
    }

    public BlockKind Kind { get; }
    public IReadOnlyList<SqlTextSpan> Opening { get; }
    public IReadOnlyList<SqlTextSpan> Closing { get; }
    public SqlTextSpan Span { get; }
}
