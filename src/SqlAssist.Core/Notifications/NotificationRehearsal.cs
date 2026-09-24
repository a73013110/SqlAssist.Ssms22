using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Notifications;

/// <summary>「關於與診斷」的通知測試：每一種情境送出哪些通知、隔多久。</summary>
/// <remarks>
/// 走正式的 <see cref="NotificationCenter"/>，不另做一條假的顯示路徑：要回答的是「在我的設定下
/// 通知看不看得到、長什麼樣子」，繞過可見度或合併就回答不了。種類是
/// <see cref="NotificationKind.Diagnostics"/>（沒有開關），來源一律 <see cref="NotificationOrigin.User"/>，
/// 跨得過詳細度門檻；總開關與失敗通道照樣管得住，那正是測試要讓人看見的。失敗情境因此也會
/// 列進「通知失敗」，種類名稱是「通知測試」，和真正的失敗分得開。
///
/// 活動用 <see cref="NotificationCenter.BeginDetached"/>：測試不是環境父工作，那三秒內同一條
/// 非同步流程上的其他查詢不能併進來。等待由呼叫端交進來，自動測試換成立即完成的版本。
/// </remarks>
public static class NotificationRehearsal
{
    /// <summary>執行中那一段的長度；看得清楚膠囊與進度，又不至於等到不耐煩。</summary>
    public static readonly TimeSpan WorkDuration = TimeSpan.FromSeconds(3);

    /// <summary>重複情境每一次的長度；逐次完成，才看得到 ×N 往上長。</summary>
    public static readonly TimeSpan RepeatDuration = TimeSpan.FromMilliseconds(400);

    public const int RepeatCount = 5;

    /// <summary>按鈕的順序；新增一種情境只動列舉、這張表與 <see cref="RunAsync"/>。</summary>
    public static IReadOnlyList<NotificationRehearsalScenario> All { get; } = new[]
    {
        NotificationRehearsalScenario.Success,
        NotificationRehearsalScenario.Failure,
        NotificationRehearsalScenario.Repeats,
        NotificationRehearsalScenario.Prompt,
        NotificationRehearsalScenario.PromptStack,
        NotificationRehearsalScenario.PromptWithActivity,
    };

    /// <summary>按鈕上的字；說出會看到什麼，而不是內部的情境名。</summary>
    public static string Label(NotificationRehearsalScenario scenario) => scenario switch
    {
        NotificationRehearsalScenario.Success => "3 秒後成功",
        NotificationRehearsalScenario.Failure => "3 秒後失敗",
        NotificationRehearsalScenario.Repeats => "連續 5 次成功",
        NotificationRehearsalScenario.Prompt => "一則提醒",
        NotificationRehearsalScenario.PromptStack => "三則提醒",
        NotificationRehearsalScenario.PromptWithActivity => "提醒加活動",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    /// <summary>目前設定會把這個情境整個藏起來時的原因；看得到時是 null。</summary>
    /// <remarks>
    /// 測試是在問「通知看不看得到」，被設定藏起來的那一次必須說出是哪一格，否則按下去沒反應
    /// 與通知壞了分不出來。只列會藏掉整個情境的開關：種類開關管不到通知測試，詳細度擋不住 User。
    /// </remarks>
    public static string? HiddenReason(NotificationRehearsalScenario scenario, SqlAssistSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (!settings.Enabled) return "「啟用 SqlAssist」關著，測試通知不會出現。";
        if (!settings.NotificationEnabled) return "「顯示通知提示」關著，測試通知不會出現。";
        return scenario == NotificationRehearsalScenario.Failure && !settings.NotificationFailures
            ? "「顯示所有種類的失敗」關著，失敗的測試不會出現。"
            : null;
    }

    /// <summary>送出一種情境；活動的那幾種在工作結束時才完成。</summary>
    /// <param name="delay">等待；正式環境是 <see cref="Task.Delay(TimeSpan)"/>。</param>
    public static async Task RunAsync(NotificationRehearsalScenario scenario, NotificationCenter center,
        Func<TimeSpan, Task> delay)
    {
        if (center is null) throw new ArgumentNullException(nameof(center));
        if (delay is null) throw new ArgumentNullException(nameof(delay));
        switch (scenario)
        {
            case NotificationRehearsalScenario.Success:
                await WorkAsync(center, delay, fail: false).ConfigureAwait(false);
                break;
            case NotificationRehearsalScenario.Failure:
                await WorkAsync(center, delay, fail: true).ConfigureAwait(false);
                break;
            case NotificationRehearsalScenario.Repeats:
                for (var round = 0; round < RepeatCount; round++)
                    using (Begin(center, NotificationCatalog.SimulatingRepeatedWork))
                        await delay(RepeatDuration).ConfigureAwait(false);
                break;
            case NotificationRehearsalScenario.Prompt:
                Prompt(center, NotificationSeverity.Info);
                break;
            case NotificationRehearsalScenario.PromptStack:
                Prompt(center, NotificationSeverity.Info);
                Prompt(center, NotificationSeverity.Warning);
                Prompt(center, NotificationSeverity.Error);
                break;
            case NotificationRehearsalScenario.PromptWithActivity:
                Prompt(center, NotificationSeverity.Info);
                await WorkAsync(center, delay, fail: false).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static async Task WorkAsync(NotificationCenter center, Func<TimeSpan, Task> delay, bool fail)
    {
        using var scope = Begin(center, NotificationCatalog.SimulatingWork);
        await delay(WorkDuration).ConfigureAwait(false);
        if (fail) scope.Fail();
    }

    private static NotificationScope Begin(NotificationCenter center, string title) =>
        center.BeginDetached(title, NotificationKind.Diagnostics, NotificationOrigin.User, NotificationLevel.Info);

    private static void Prompt(NotificationCenter center, NotificationSeverity severity) =>
        center.Prompt(NotificationCatalog.RehearsalPrompt(severity), NotificationKind.Diagnostics,
            NotificationOrigin.User, NotificationLevel.Info);
}
