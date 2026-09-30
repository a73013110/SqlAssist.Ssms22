using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SqlAssist.CompletionAudit;

/// <summary>
/// 召回語料 <c>RecallCorpus.sql</c>：每一段是一句，段與段以空白行分開。
/// </summary>
/// <remarks>
/// 語料照慣例把字寫成大寫、名稱寫成大小寫混合，所以全大寫的詞一律當字稽核。
/// 刻意不收的寫法不進語料，理由寫在那一格的文件裡；這裡沒有豁免名單。
/// </remarks>
public sealed class RecallCorpusSource : IAuditCorpusSource
{
    public const string SourceName = "recall";

    private readonly string _path;

    public RecallCorpusSource(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Name => SourceName;

    public IEnumerable<AuditFragment> Read(CancellationToken cancellationToken)
    {
        var lines = File.ReadAllText(_path).Replace("\r", string.Empty).Split('\n');
        var block = new List<string>();
        var firstLine = 0;

        for (var number = 0; number <= lines.Length; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (number < lines.Length && lines[number].Trim().Length > 0)
            {
                if (block.Count == 0)
                {
                    firstLine = number + 1;
                }

                block.Add(lines[number]);
                continue;
            }

            if (block.Count > 0)
            {
                yield return new AuditFragment(SourceName, $"{SourceName}:{firstLine}", string.Join("\n", block), wordsInUpperCase: true);
                block.Clear();
            }
        }
    }
}
