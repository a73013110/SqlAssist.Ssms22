using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlAssist.Core.Parsing;

/// <summary>每份文件的有界背景工作器；debounce、等待中的取消與解析互斥可獨立測試。</summary>
public sealed class BlockAnalysisWorker
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BlockMatcher> AnalyzeAsync(Func<string> readText, int delayMilliseconds, CancellationToken cancellationToken)
    {
        if (readText is null) throw new ArgumentNullException(nameof(readText));
        if (delayMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
        await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 取得完整文字也在背景；取消的等待者不會先複製一份大型快照。
                var text = readText();
                return new BlockMatcher(text, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
