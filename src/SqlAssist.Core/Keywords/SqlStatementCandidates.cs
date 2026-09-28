using System;
using System.Collections.Generic;

namespace SqlAssist.Core.Keywords;

/// <summary>
/// 這一份建議清單裡的關鍵字候選，各自對到哪一份語句說明。
/// </summary>
/// <remarks>
/// 一個候選字是不是一句的開頭，要看它前面的文字（<see cref="SqlBuiltInDocCatalog.TryGetStatementFor"/>），
/// 而那一段在同一份清單裡不會變。每換一次選取，說明面板與浮動預覽各問一次；記下答案，
/// 按著方向鍵路過的同一個字只分析一次；不在語句字表裡的候選查表就擋掉，不花詞法分析。
///
/// 說明面板在背景執行緒上問、浮動預覽在 UI 執行緒上問，所以查表上鎖。
/// </remarks>
public sealed class SqlStatementCandidates
{
    private readonly string _text;
    private readonly int _position;
    private readonly Dictionary<string, SqlBuiltInDoc?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="text">整份指令碼。</param>
    /// <param name="position">這一份清單要取代的那個字的起點。</param>
    public SqlStatementCandidates(string text, int position)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _position = position;
    }

    public bool TryGet(string candidate, out SqlBuiltInDoc doc)
    {
        lock (_resolved)
        {
            if (!_resolved.TryGetValue(candidate, out var resolved))
            {
                resolved = SqlBuiltInDocCatalog.TryGetStatementFor(_text, _position, candidate, out var found)
                    ? found
                    : null;
                _resolved[candidate] = resolved;
            }

            doc = resolved!;
            return resolved is not null;
        }
    }
}
