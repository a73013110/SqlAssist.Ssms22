using System;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 一次等儲存的使用者操作：拒絕重入、記下宿主世代，回來時仍屬於同一份儲存才套用結果。
/// </summary>
/// <remarks>
/// 清單列操作與版本時間軸共用：連按刪除或回溯只會送出一次，切頁、停用或換了儲存之後才回來的
/// 成功與失敗都不回報，不讓舊儲存的結果動到新畫面。
/// </remarks>
internal sealed class SqlMemoryOperationGate
{
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <param name="verb">失敗訊息的動作名稱。</param>
    /// <param name="work">背景部分；回傳在 UI 執行緒套用結果的動作。</param>
    public async Task RunAsync(CancellationToken token, Action<string> report, string verb, Func<Task<Action>> work)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        var generation = SqlMemoryHost.Runtime.Generation;
        Action? apply = null;
        try
        {
            apply = await work();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 切頁或停用後才回來的失敗不能蓋掉新頁面的訊息；取消本來就不回報。
            if (IsCurrent(generation, token)) report(SqlMemoryTimeText.Failure(verb, error));
            return;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
        if (IsCurrent(generation, token)) apply();
    }

    public static bool IsCurrent(long generation, CancellationToken token) =>
        !token.IsCancellationRequested && SqlMemoryHost.Runtime.IsAvailable && generation == SqlMemoryHost.Runtime.Generation;
}
