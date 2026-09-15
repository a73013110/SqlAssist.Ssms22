using System;
using SqlAssist.Core.SqlMemory;
using Xunit;
using static SqlAssist.Core.Tests.SqlMemory.SqlMemoryTestData;

namespace SqlAssist.Core.Tests.SqlMemory;

/// <summary>
/// 量測大型 SQL（約 10 MB）連續 idle 擷取的寫入計畫：內容不變時不得再產生 Content／Recovery，
/// 內容真的變動時仍要照常寫入。對照 SqlAssist.SqlMemory.Sqlite.Tests 專案下量到 commit
/// 耗時與資料列數的對應測試。
/// </summary>
public sealed class SqlCapturePlannerPerformanceTests
{
    // 5,242,880 字元 × 2 bytes（UTF-16LE）= 10 MB，對應要求描述的「10 MB SQL」情境。
    private const int LargeTextChars = 5 * 1024 * 1024;

    [Fact]
    public void RepeatedUnchangedIdleCapturesStopProducingWritesAndStayCheap()
    {
        var planner = new SqlCapturePlanner();
        var text = new string('a', LargeTextChars);
        var first = planner.Prepare(Capture(text: text), null, Policy) ?? throw new InvalidOperationException();
        Assert.NotEmpty(first.Contents);
        Assert.NotNull(first.Recovery);

        var state = first.State;
        // 先跑一次讓 Recovery 已經指向目前內容；之後才是「內容不變」的穩態。
        var warm = planner.Prepare(Capture(2, text, seconds: 5), state, Policy) ?? throw new InvalidOperationException();
        Assert.Empty(warm.Contents);
        Assert.Null(warm.Recovery);
        state = warm.State;

        var sequence = 3L;
        var allocated = AllocationProbe.MeasureSteadyState(() =>
        {
            var write = planner.Prepare(Capture(sequence, text, seconds: (int)sequence * 5), state, Policy)
                ?? throw new InvalidOperationException("穩態擷取不應被序號或 Session 檢查擋下。");
            Assert.Empty(write.Contents);
            Assert.Null(write.Recovery);
            state = write.State;
            sequence++;
        }, iterations: 10);

        // 引擎仍會為了判斷「有沒有變」而雜湊全文（避免不了），但不再複製或編碼整份文字去寫入；
        // 配置量應遠小於複製一次 10 MB 文字（更遠小於編碼成 UTF-16LE BLOB 的兩倍位元組數）。
        Assert.True(allocated < text.Length,
            $"預期不變內容的 idle 擷取不再複製全文，實際配置 {allocated} bytes（文字長度 {text.Length} 字元）。");
    }

    [Fact]
    public void RepeatedSmallEditsKeepProducingRecoveryWrites()
    {
        var planner = new SqlCapturePlanner();
        var baseline = new string('a', LargeTextChars);
        SqlSessionHead? state = null;
        for (var i = 1; i <= 5; i++)
        {
            var text = baseline + i; // 每次小幅修改：內容必須真的不同，不能被跳過寫入的分支擋下。
            var write = planner.Prepare(Capture(i, text, seconds: i * 5), state, Policy)
                ?? throw new InvalidOperationException();
            Assert.NotNull(write.Recovery);
            Assert.Equal(write.Recovery!.ContentId, write.State.RecoveryContentId);
            state = write.State;
        }
    }
}
