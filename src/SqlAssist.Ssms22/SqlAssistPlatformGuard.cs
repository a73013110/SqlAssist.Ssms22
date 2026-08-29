using System;
using System.Threading.Tasks;

namespace SqlAssist.Ssms22;

/// <summary>
/// 平台呼叫進來的那一層邊界，一律不讓例外跑出去。
/// </summary>
/// <remarks>
/// MEF 的建立方法、編輯器事件、按鍵處理常式與排進派送佇列的工作都由 SSMS 直接呼叫，
/// 例外從這些地方跑出去的下場不是擴充失效就是使用者眼前跳出錯誤對話框——而症狀通常是
/// 「整個查詢視窗打不開」或「Tab 突然不能按」，跟真正的錯誤看起來毫無關係。
/// 每一個入口各寫一次 try／catch 的問題不在重複，在於新增一個 handler 時
/// 很容易忘記寫，而忘記寫不會有任何徵兆。
///
/// 邊界的行為分成三族，選錯的症狀各不相同：
///
/// <list type="bullet">
/// <item><see cref="Run(string, Action)"/> 一族：不該發生的失敗，一律留下完整堆疊。</item>
/// <item><see cref="Probe{T}(string, Func{T}, T)"/> 一族：向平台問一件可有可無的事
/// （佈景筆刷、DPI、游標位置、預先載入的中繼資料），失敗是預期內的，只在詳細診斷
/// 打開時記一行。用 Run 記的話，這些每秒都可能失敗一次的呼叫會把紀錄檔灌滿，
/// 真正的錯誤就埋在裡面找不到了。</item>
/// <item><see cref="Begin(string, Func{Task})"/> 一族：沒有人會去接結果的背景工作。</item>
/// </list>
///
/// 取消一律當成正常結束：這條路上的取消不是編輯器關閉就是平台換了下一輪，
/// 兩者都是「這一輪什麼都不做」。唯一的例外是
/// <see cref="RunPropagatingCancellation{T}"/>，理由寫在那裡。
///
/// 只給平台邊界用。Core 與 Metadata 的商業邏輯錯誤該讓它浮出來，
/// 吞掉之後就只剩「功能安靜地不作用」這一種症狀。
/// 需要讓使用者看見的失敗也不走這裡——工具選單的命令、預覽視窗的狀態列與
/// Snippet 管理員都要把原因顯示出來，而訊息一句一句都不一樣。
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
        catch (OperationCanceledException)
        {
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
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
            return fallback;
        }
    }

    /// <inheritdoc cref="Run{T}(string, Func{T}, T)"/>
    public static async Task<T> RunAsync<T>(string operation, Func<Task<T>> work, T fallback)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
            return fallback;
        }
    }

    /// <summary>
    /// 同 <see cref="Run{T}(string, Func{T}, T)"/>，但把取消還給平台。
    /// </summary>
    /// <param name="fallback">
    /// 替代值等到真的失敗才算。這一族的替代值往往本身就要走一趟完整清單，
    /// 傳值的話每一次成功也要先付一次——那是使用者每按一個鍵就白付一次。
    /// </param>
    /// <remarks>
    /// 只有一種情形要這樣做：平台開了一個非同步工作，並且靠回傳的 Task 是不是
    /// 取消狀態來判斷這一輪的結果作廢。吞掉取消再回傳替代值，
    /// 等於把一份已經過期的內容交回去當成有效答案。
    /// </remarks>
    public static T RunPropagatingCancellation<T>(string operation, Func<T> work, Func<T> fallback)
    {
        try
        {
            return work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
            return fallback();
        }
    }

    /// <summary>建立失敗就不參與，回傳 null。</summary>
    public static T? Create<T>(string operation, Func<T?> work)
        where T : class
    {
        return Run<T?>(operation, work, fallback: null);
    }

    /// <summary>
    /// 向平台問一件可有可無的事；失敗只在詳細診斷打開時記一行。
    /// </summary>
    /// <param name="fallback">問不到時的替代值，必須本身就是可用的答案。</param>
    public static T Probe<T>(string operation, Func<T> work, T fallback)
    {
        try
        {
            return work();
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.Write($"{operation}失敗：{exception.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// 沒有回傳值的探測：呼叫端在呼叫前就已經備妥可用的值，這裡只是試著換成更好的。
    /// </summary>
    public static void Probe(string operation, Action work)
    {
        Probe(
            operation,
            () =>
            {
                work();
                return true;
            },
            fallback: false);
    }

    /// <summary>起一個沒有人會去接結果的背景工作。</summary>
    public static void Begin(string operation, Func<Task> work)
    {
        _ = AwaitAsync(operation, work, expected: false);
    }

    /// <inheritdoc cref="Begin(string, Func{Task})"/>
    public static void Begin(string operation, Action work)
    {
        Begin(operation, () => Task.Run(work));
    }

    /// <summary>
    /// 背景做一件可有可無的事——預先載入、預熱連線、重新確認狀態。
    /// </summary>
    /// <remarks>
    /// 這些工作失敗只代表下一次按鍵要自己付一次成本，功能本身沒有壞。
    /// 連線斷掉時它們會連續失敗，所以紀錄層級跟 <see cref="Probe{T}"/> 一樣。
    /// </remarks>
    public static void BeginProbe(string operation, Func<Task> work)
    {
        _ = AwaitAsync(operation, work, expected: true);
    }

    /// <inheritdoc cref="BeginProbe(string, Func{Task})"/>
    public static void BeginProbe(string operation, Action work)
    {
        BeginProbe(operation, () => Task.Run(work));
    }

    private static async Task AwaitAsync(string operation, Func<Task> work, bool expected)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (expected)
            {
                SqlAssistDiagnostics.Write($"{operation}失敗：{exception.Message}");
                return;
            }

            SqlAssistDiagnostics.WriteAlways($"{operation}失敗：{exception}");
        }
    }
}
