using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.KeywordGenerator;

/// <summary>探測完的收尾：唯一的接續併成一項，同結果的位置併成一個片語。</summary>
internal static class PhraseMerging
{
    /// <summary>
    /// 唯一接得下去的字併成一項：ASYMMETRIC 之後只有 KEY、ENCRYPTION 之後只有 BY，清單列的就是
    /// ASYMMETRIC KEY、ENCRYPTION BY PASSWORD，選一次寫完。
    /// </summary>
    /// <remarks>
    /// 條件是那個字寫到這裡還沒完整、封閉、接不了名稱、值或括號，而它之後正好一個字；那個字照同一條規則再往下併。
    /// 中間每一段的片語照舊：一個字一個字打的人看到的是同一條路。每一條都照併之前的字判，最後一起換掉。
    /// </remarks>
    public static void ChainUniqueContinuations(PhraseTable phrases)
    {
        var chained = phrases.Values
            .Select(phrase => phrase.Words.Select(word => Chain(phrases, phrase.After[0], phrase.Pattern, word)).ToList())
            .ToList();

        for (var index = 0; index < chained.Count; index++)
        {
            phrases.Values[index].Words = chained[index];
        }
    }

    /// <summary>
    /// 同一條尾巴在幾個位置上探到一模一樣的結果時併成一個片語，位置取聯集；結果不同的
    /// （資料表之後的 FOR 多一個 SYSTEM_TIME）各自一個，執行期由前一格的位置分開。
    /// </summary>
    public static List<ProbedPhrase> MergePositions(IEnumerable<ProbedPhrase> phrases)
    {
        var byResult = new Dictionary<string, ProbedPhrase>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<ProbedPhrase>();

        foreach (var phrase in phrases)
        {
            var key = string.Join("\t", phrase.Pattern, phrase.Closed, phrase.TakesVariable, phrase.EndsStatement, string.Join(" ", phrase.Words));

            if (byResult.TryGetValue(key, out var first))
            {
                first.After.AddRange(phrase.After);
                continue;
            }

            var copy = new ProbedPhrase(phrase.Pattern, phrase.After[0], phrase.Probe, phrase.Words)
            {
                Closed = phrase.Closed,
                TakesVariable = phrase.TakesVariable,
                EndsStatement = phrase.EndsStatement,
                TakesOperand = phrase.TakesOperand,
            };

            copy.After.AddRange(phrase.After.Skip(1));
            byResult.Add(key, copy);
            merged.Add(copy);
        }

        return merged;
    }

    private static string Chain(PhraseTable phrases, string after, string pattern, string word)
    {
        if (!phrases.TryGet(ProbedPhrase.Key(after, pattern.Length > 0 ? pattern + " " + word : word), out var next) ||
            next.EndsStatement || !next.Closed || next.TakesOperand || next.Words.Count != 1)
        {
            return word;
        }

        return word + " " + Chain(phrases, after, next.Pattern, next.Words[0]);
    }
}
