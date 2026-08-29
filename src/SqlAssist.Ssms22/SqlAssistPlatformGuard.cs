using System;
using System.Threading.Tasks;

namespace SqlAssist.Ssms22;

/// <summary>
/// 平台呼叫進來的那一層邊界，一律不讓例外跑出去。
/// </summary>
/// <remarks>
/// MEF 的建立方法、編輯器事件與按鍵處理常式都由 SSMS 直接呼叫，例外從這些地方
/// 跑出去的下場不是擴充失效就是使用者眼前跳出錯誤對話框——而症狀通常是
/// 「整個查詢視窗打不開」或「Tab 突然不能按」，跟真正的錯誤看起來毫無關係。
/// 每一個入口各寫一次 try／catch 的問題不在重複，在於新增一個 handler 時
/// 很容易忘記寫，而忘記寫不會有任何徵兆。
///
/// 只給平台邊界用。Core 與 Metadata 的商業邏輯錯誤該讓它浮出來，
/// 吞掉之後就只剩「功能安靜地不作用」這一種症狀。
/// 需要回報給使用者的地方（工具選單的命令）也不走這裡：那些要顯示訊息框，
/// 而訊息一句一句都不一樣。
/// </remarks>
internal static class SqlAssistPlatformGuard
{
    /// <param name="operation">寫進紀錄的操作名稱，例如「更新萬用字元提示」。</param>
    public static void Run(string operation, Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
        }
    }

    /// <param name="fallback">失敗時回傳的值，必須是「這一輪什麼都不做」的意思。</param>
    public static T Run<T>(string operation, Func<T> work, T fallback)
    {
        try
        {
            return work();
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
            return fallback;
        }
    }

    /// <summary>建立失敗就不參與，回傳 null。</summary>
    public static T? Create<T>(string operation, Func<T?> work)
        where T : class
    {
        return Run<T?>(operation, work, fallback: null);
    }

    /// <summary>
    /// 起一個沒有人會去接結果的背景工作。
    /// </summary>
    /// <remarks>
    /// 取消當成正常結束：這條路上的取消一律來自編輯器關閉。
    /// </remarks>
    public static void Begin(string operation, Func<Task> work)
    {
        _ = RunAsync(operation, work);
    }

    private static async Task RunAsync(string operation, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 編輯器已關閉。
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
        }
    }
}
