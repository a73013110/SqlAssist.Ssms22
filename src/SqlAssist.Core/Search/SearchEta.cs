using System;

namespace SqlAssist.Core.Search;

/// <summary>
/// 一輪搜尋還要多久；說不準時不說。
/// </summary>
/// <remarks>
/// <b>寧可晚出現，也不出現一個會跳的數字。</b>工時評估的人會拿這個數字決定要不要等，
/// 而「約 3 秒」之後又變成「約 40 秒」比什麼都不寫更傷信任。所以：
/// 至少跑了 <see cref="MinimumElapsed"/>、而且走了 <see cref="MinimumFraction"/> 才開始算；
/// 每一次的估計以指數平滑收斂，不直接換成最新那一次的外推；
/// 顯示時進位到 <see cref="Step"/> 的倍數，不寫出假的精確度。
///
/// 進度倒退（新宣告了目標）時整份重來：舊的速率是對另一個分母算的。
/// </remarks>
public sealed class SearchEta
{
    public static readonly TimeSpan MinimumElapsed = TimeSpan.FromSeconds(2);

    public const double MinimumFraction = 0.1;

    /// <summary>顯示的顆粒度；預估時間一律進位到它的倍數。</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    /// <summary>新一次估計佔多少；越小越穩、越慢跟上真實速率。</summary>
    private const double Smoothing = 0.3;

    private double _lastFraction;
    private double? _remainingSeconds;

    /// <summary>
    /// 餵一次進度，回傳要顯示的剩餘時間；還說不準時為 null。
    /// </summary>
    /// <param name="elapsed">這一輪開始到現在。</param>
    /// <param name="fraction">整輪的進度，0 到 1。</param>
    public TimeSpan? Observe(TimeSpan elapsed, double fraction)
    {
        if (double.IsNaN(fraction) || fraction < _lastFraction)
        {
            _remainingSeconds = null;
        }

        _lastFraction = double.IsNaN(fraction) ? 0 : fraction;

        if (elapsed < MinimumElapsed || fraction < MinimumFraction || fraction >= 1) return null;

        var estimate = elapsed.TotalSeconds * (1 - fraction) / fraction;
        _remainingSeconds = _remainingSeconds is { } previous
            ? previous + Smoothing * (estimate - previous)
            : estimate;

        var steps = Math.Max(1, Math.Ceiling(_remainingSeconds.Value / Step.TotalSeconds));
        return TimeSpan.FromSeconds(steps * Step.TotalSeconds);
    }

    /// <summary>新的一輪開始；上一輪的速率不代表這一輪。</summary>
    public void Reset()
    {
        _lastFraction = 0;
        _remainingSeconds = null;
    }
}
