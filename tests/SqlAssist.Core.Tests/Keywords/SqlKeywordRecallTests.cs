using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.CompletionAudit;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Keywords;

/// <summary>
/// 召回稽核：合法的 T-SQL 裡每一個詞，在它的起點都列得出來。
/// </summary>
/// <remarks>
/// 位置分析與片語都是「這一格接哪些字」的推論，推錯的症狀是使用者要的字不見了，而那不會讓
/// 任何一個針對單一位置寫的測試失敗。這裡反過來拿真的寫得出來的語句問：語料
/// （<c>RecallCorpus.sql</c>）每一段是一句，在每一個詞的起點走一次產品的路徑，那個詞要在清單裡看得到，
/// 而且那一格的清單要開得起來。判定與 <c>tools/Audit-Completions.ps1</c> 是同一份
/// （<see cref="CompletionAuditor"/>）；語料沒有資料庫，名稱只稽核指令碼自己取過的那些。
///
/// 沒有豁免名單：列不出來的就修掉。刻意不收的寫法不進語料，理由寫在那一格的文件裡。
/// </remarks>
public sealed class SqlKeywordRecallTests
{
    [Fact]
    public async Task 語料裡的每一個詞在它的起點列得出來()
    {
        var auditor = new CompletionAuditor(
            new SqlAssistSettings(),
            BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current),
            AuditMask.None);
        var misses = new List<AuditMiss>();
        var unparsed = new List<string>();

        foreach (var fragment in new RecallCorpusSource(DataPath("RecallCorpus.sql")).Read(CancellationToken.None))
        {
            var result = await auditor.AuditAsync(fragment, AuditCatalog.None, CancellationToken.None);
            misses.AddRange(result.Misses);

            if (result.Tally.UnparsedStatements > 0)
            {
                unparsed.Add(fragment.Id);
            }
        }

        // 剖析不過的句子不稽核：語料要是合法的 T-SQL，否則零漏守不到那一句。
        Assert.True(unparsed.Count == 0, "剖析不過的段：" + string.Join("、", unparsed));
        Assert.True(
            misses.Count == 0,
            "列不出來的詞：" + Environment.NewLine + string.Join(
                Environment.NewLine,
                misses.Select(miss => $"  {miss.Example.Replace("\n", " ")}（{miss.Kind}）")));
    }

    private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Keywords", name);
}
