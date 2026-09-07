using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAssist.Core.Parsing;

public static partial class SqlTokenizer
{
    /// <summary>供完整快照分析的官方詞法串流；保留字串以免錯接複合關鍵字。</summary>
    internal static IReadOnlyList<SqlToken> TokenizeBlocks(string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new StringReader(sql);
        var stream = new TSql160Parser(true).GetTokenStream(reader, out _);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<SqlToken>(stream.Count);
        foreach (var token in stream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
                or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile)
            {
                continue;
            }

            var kind = token.TokenType switch
            {
                TSqlTokenType.LeftParenthesis or TSqlTokenType.RightParenthesis => SqlTokenKind.Punctuation,
                TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral => SqlTokenKind.String,
                // 同名欄位不是批次分隔；只有 ScriptDom 判定的 Go 才能清除未閉合堆疊。
                TSqlTokenType.Identifier when string.Equals(token.Text, "GO", System.StringComparison.OrdinalIgnoreCase) => SqlTokenKind.Operator,
                _ => SqlTokenKind.Identifier
            };
            // ScriptDom 已辨識識別字的真正結尾；不自行掃描跳脫的 ]]。
            var quoted = token.TokenType == TSqlTokenType.QuotedIdentifier;
            result.Add(new SqlToken(kind, token.Offset, token.Text.Length, token.Text, token.Text, quoted));
        }

        return result;
    }
}
