using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

/// <summary>
/// 量測大型 SQL（約 10 MB）連續 idle 擷取對 SQLite commit 的實際成本：內容不變時應該幾乎不花
/// 時間、也不再讓 Contents／Recovery 反覆增刪；內容每次小幅修改時則是預期中的真實工作量，
/// 兩者拿來互相對照，而不是量測不穩定的絕對毫秒數。
/// </summary>
public sealed class SqlCapturePerformanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // 5,242,880 字元 × 2 bytes（UTF-16LE）= 10 MB。
    private const int LargeTextChars = 5 * 1024 * 1024;
    private const int Ticks = 20;

    // 關掉 auto revision：讓兩段量測都只碰 Recovery，不會有第一筆擷取順便釘住一筆 Revision
    // 內容，跟既有 RecoveryOnlyKeepsLatestContentWithoutOrphanGrowth 用的政策一致。
    private static readonly SqlCapturePolicy RecoveryOnlyPolicy = new(true, false, TimeSpan.FromMinutes(10), true, true);

    [Fact]
    public async Task UnchangedIdleCapturesStopRewritingRecoveryAndCommitFasterThanRealEdits()
    {
        var text = new string('a', LargeTextChars);

        // 先在另一個資料庫暖機：JIT、SQLite 檔案子系統與兩條 WriteContent 分支（命中／未命中）
        // 都先跑過一次，避免兩段量測誰先跑就吃到冷啟動成本而讓比較失真。
        using (var warmupStore = new SqliteTestStore())
        {
            var warmupRepository = await warmupStore.Open(Token);
            await warmupStore.Process(warmupRepository, warmupStore.Capture(1, "SELECT 1;", SqlCaptureKind.DraftIdle), Token, RecoveryOnlyPolicy);
            await warmupStore.Process(warmupRepository, warmupStore.Capture(2, "SELECT 1;", SqlCaptureKind.DraftIdle, seconds: 5), Token, RecoveryOnlyPolicy);
            await warmupStore.Process(warmupRepository, warmupStore.Capture(3, "SELECT 2;", SqlCaptureKind.DraftIdle, seconds: 10), Token, RecoveryOnlyPolicy);
        }

        using var unchangedStore = new SqliteTestStore();
        var unchangedRepository = await unchangedStore.Open(Token);
        await unchangedStore.Process(unchangedRepository, unchangedStore.Capture(1, text, SqlCaptureKind.DraftIdle), Token, RecoveryOnlyPolicy);
        Assert.Equal(1L, unchangedStore.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, unchangedStore.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(0L, unchangedStore.Scalar("SELECT count(*) FROM Revisions;"));

        var unchangedTimer = Stopwatch.StartNew();
        for (var i = 2; i <= Ticks; i++)
            await unchangedStore.Process(unchangedRepository, unchangedStore.Capture(i, text, SqlCaptureKind.DraftIdle, seconds: i * 5), Token, RecoveryOnlyPolicy);
        unchangedTimer.Stop();

        // 內容沒變：不得再產生新的 Content 或改寫 Recovery；Session 仍照 Sequence 前進以維持 CAS。
        Assert.Equal(1L, unchangedStore.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, unchangedStore.Scalar("SELECT count(*) FROM Recovery;"));
        var unchangedSession = await unchangedRepository.ReadSessionAsync(unchangedStore.Session.SessionId, Token);
        Assert.Equal((long)Ticks, unchangedSession?.LastSequence);

        using var editedStore = new SqliteTestStore();
        var editedRepository = await editedStore.Open(Token);
        await editedStore.Process(editedRepository, editedStore.Capture(1, text, SqlCaptureKind.DraftIdle), Token, RecoveryOnlyPolicy);

        var editedTimer = Stopwatch.StartNew();
        for (var i = 2; i <= Ticks; i++)
            await editedStore.Process(editedRepository, editedStore.Capture(i, text + i, SqlCaptureKind.DraftIdle, seconds: i * 5), Token, RecoveryOnlyPolicy);
        editedTimer.Stop();

        // 每次真的小幅修改：內容確實不同，但舊 Recovery 內容沒有其他引用，跟既有
        // RecoveryOnlyKeepsLatestContentWithoutOrphanGrowth 一樣只留最新一筆，不堆積孤兒列。
        Assert.Equal(1L, editedStore.Scalar("SELECT count(*) FROM Contents;"));
        Assert.Equal(1L, editedStore.Scalar("SELECT count(*) FROM Recovery;"));
        Assert.Equal(0L, editedStore.Scalar("SELECT count(*) FROM Revisions;"));

        // 不變內容的 idle 擷取應該明顯快於每次真的改動 10 MB 內容；用比例而非絕對時間避免環境差異造成不穩，
        // 兩者在同一個測試程序內背靠背量測，環境雜訊對兩邊的影響應該相近。
        Assert.True(unchangedTimer.Elapsed < editedTimer.Elapsed,
            $"預期不變內容擷取（{unchangedTimer.Elapsed.TotalMilliseconds:F1} ms）快於每次真的改動 10 MB 內容（{editedTimer.Elapsed.TotalMilliseconds:F1} ms）。");
    }
}
