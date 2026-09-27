using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 一份指令碼的語句界線，以及每個 FROM 接不接資料來源。
/// </summary>
/// <remarks>
/// 判準只有 <see cref="SqlKeywordPositionAnalyzer"/> 一份（<c>IsStatementHead</c>、
/// <c>IntroducesDataSource</c>）；這裡把它交給看得到游標後方文字的呼叫端：範圍分析與欄位來源解析。
/// 各寫一份關鍵字名單的症狀是名單外的語句（BACKUP、RESTORE、THROW、BEGIN）不是界線，
/// 上一句的範圍延伸進來，下一句的資料來源也被收進上一句。
///
/// 需要原文：隱含的界線是「子句寫完又換了行」，換行只有原文有。判過的語句開頭記在分析器裡，
/// 同一份文字共用一個執行個體。
/// </remarks>
internal sealed class SqlStatementBoundaries
{
    private readonly SqlKeywordPositionAnalyzer analyzer;

    /// <summary>補上的運算元在分析用詞元裡的索引；沒有補就是 <see cref="int.MaxValue"/>。</summary>
    private readonly int operand;

    /// <param name="text">整份指令碼。</param>
    /// <param name="tokens"><paramref name="text"/> 的詞元。</param>
    /// <param name="caretPosition">
    /// 使用者正在寫的位置；-1 是沒有游標（滑鼠停留、整份掃描）。
    /// </param>
    /// <remarks>
    /// 游標那一格當成一個寫完的運算元。游標前的子句幾乎總是停在半途（<c>WHERE |</c>、
    /// <c>ON source.|</c>），而隱含的界線要子句寫完才成立——不補的話下一行的 <c>UPDATE</c>、
    /// <c>RESTORE</c> 併進游標這一句，範圍多收一張表，<c>source.RESTORE</c> 還被讀成名稱。
    /// 使用者要寫的正是那個運算元，所以判界線時在游標處補一個零長度的名稱；
    /// 游標貼著名稱、變數或常值時那個詞元自己就是，不補。只影響界線，資料來源仍從原本的詞元讀。
    /// </remarks>
    public SqlStatementBoundaries(string text, IReadOnlyList<SqlToken> tokens, int caretPosition = -1)
    {
        Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        operand = FindOperandIndex(tokens, caretPosition);

        analyzer = SqlKeywordPositionAnalyzer.ForScript(
            operand == int.MaxValue ? tokens : WithOperand(tokens, operand, caretPosition),
            text);
    }

    /// <summary>原本的詞元；索引都以這一份為準。</summary>
    public IReadOnlyList<SqlToken> Tokens { get; }

    /// <summary><paramref name="index"/> 是一句的開頭，判準與位置分析相同。</summary>
    public bool IsStatementHead(int index) => analyzer.IsStatementHead(Map(index));

    /// <summary><paramref name="from"/> 的 FROM 後面接資料來源：它所屬的動詞是 SELECT、UPDATE 或 DELETE。</summary>
    public bool IntroducesDataSource(int from) => analyzer.IntroducesDataSource(Map(from));

    private int Map(int index) => index >= operand ? index + 1 : index;

    /// <summary>游標處要補運算元時，它在分析用詞元裡的索引；不補就是 <see cref="int.MaxValue"/>。</summary>
    private static int FindOperandIndex(IReadOnlyList<SqlToken> tokens, int caretPosition)
    {
        if (caretPosition < 0)
        {
            return int.MaxValue;
        }

        var index = 0;

        while (index < tokens.Count && tokens[index].End < caretPosition)
        {
            index++;
        }

        // 游標在詞元裡面，或貼著一個本身就是運算元的詞元（前後都算：正在打的字可能在游標兩側）。
        for (var near = index; near < tokens.Count && tokens[near].Start <= caretPosition; near++)
        {
            var token = tokens[near];

            if (token.Start < caretPosition && caretPosition < token.End)
            {
                return int.MaxValue;
            }

            if (token.Kind is SqlTokenKind.Identifier or SqlTokenKind.Variable or SqlTokenKind.Number or SqlTokenKind.String)
            {
                return int.MaxValue;
            }
        }

        while (index < tokens.Count && tokens[index].Start < caretPosition)
        {
            index++;
        }

        return index;
    }

    private static IReadOnlyList<SqlToken> WithOperand(IReadOnlyList<SqlToken> tokens, int operand, int caretPosition)
    {
        var result = new List<SqlToken>(tokens.Count + 1);

        for (var index = 0; index < tokens.Count; index++)
        {
            if (index == operand)
            {
                result.Add(new SqlToken(SqlTokenKind.Identifier, caretPosition, 0, string.Empty, string.Empty, isQuoted: false));
            }

            result.Add(tokens[index]);
        }

        if (operand >= tokens.Count)
        {
            result.Add(new SqlToken(SqlTokenKind.Identifier, caretPosition, 0, string.Empty, string.Empty, isQuoted: false));
        }

        return result;
    }
}
