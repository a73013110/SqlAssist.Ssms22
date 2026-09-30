using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Core.Keywords;

/// <summary>一份指令碼裡每一句的第一個詞元。</summary>
/// <remarks>
/// 界線的判準只有位置分析那一份（<see cref="SqlStatementBoundaries"/>）：分號、<c>GO</c> 之後，以及
/// 沒有分號時由動詞隱含的界線。給看得到整份文字、要逐句處理的呼叫端（召回稽核）用，
/// 不另寫一份語句切分——各寫一份的症狀是名單外的語句（BACKUP、THROW）不是界線。
/// </remarks>
public static class SqlStatementHeads
{
    /// <summary>每一句第一個詞元在 <paramref name="tokens"/> 裡的索引，由前往後。</summary>
    /// <param name="text">整份指令碼。</param>
    /// <param name="tokens"><paramref name="text"/> 的詞元。</param>
    public static IReadOnlyList<int> Find(string text, IReadOnlyList<SqlToken> tokens)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        var boundaries = new SqlStatementBoundaries(text, tokens);
        var heads = new List<int>();
        var separated = true;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.IsPunctuation(";") || token.IsKeyword("GO"))
            {
                separated = true;
                continue;
            }

            if (separated || boundaries.IsStatementHead(index))
            {
                heads.Add(index);
            }

            separated = false;
        }

        return heads;
    }
}
