using System;

namespace SqlAssist.Core.Tests;

/// <summary>
/// 量測一段熱路徑在穩態下的配置量。
/// </summary>
/// <remarks>
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> 只算本執行緒，卻不是只算本執行緒做的事：
/// 背景 GC 起頭的暫停會回收每條執行緒手上沒用完的配置區塊，那段剩餘（最多一個配置量子，
/// 約 8 KB）會被算進本執行緒的累計值。整場測試是多執行緒並行，別的測試配置得夠兇就會觸發
/// 背景 GC，於是一個完全不配置的迴圈也可能量到幾 KB。背景 GC 的
/// <see cref="GC.CollectionCount(int)"/> 要到整輪結束才加，量測視窗內看起來仍是「沒有 GC」，
/// 所以連「這回合有沒有 GC」都擋不掉這種偏差。
/// 偏差是一次性的，而且只會加不會減，取多回合最小值就濾得掉；真正的每次呼叫配置會出現在
/// 每一回合，最小值蓋不過去。
/// </remarks>
internal static class AllocationProbe
{
    public static long MeasureSteadyState(Action hotPath, int iterations, int rounds = 5)
    {
        if (hotPath is null) throw new ArgumentNullException(nameof(hotPath));
        // 先暖機，讓分層 JIT 與編譯器產生的靜態委派快取在量測開始前就位。
        for (var i = 0; i < iterations; i++) hotPath();
        var least = long.MaxValue;
        for (var round = 0; round < rounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++) hotPath();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }
}
