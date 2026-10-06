namespace SqlAssist.Core.Parsing;

/// <summary>原文裡的一段半開區間。</summary>
/// <remarks>
/// 區塊配對的端點、內建名稱佔的那幾個字、Ctrl＋點擊的連結都是這個形狀；各寫一份的話，
/// 「End 含不含最後一個字」這種約定遲早會分岔。
/// </remarks>
public readonly struct SqlTextSpan
{
    public SqlTextSpan(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Start { get; }

    public int Length { get; }

    public int End => Start + Length;

    public bool Contains(int position) => position >= Start && position < End;

    /// <summary>從 <paramref name="start"/> 到 <paramref name="end"/>（不含）。</summary>
    public static SqlTextSpan FromBounds(int start, int end) => new(start, end - start);
}
