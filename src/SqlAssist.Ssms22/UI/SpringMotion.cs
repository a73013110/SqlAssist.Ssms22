using System;
using System.Windows.Media;

namespace SqlAssist.Ssms22.UI;

/// <summary>彈簧的參數：多久回應（週期，秒）與阻尼比。</summary>
/// <remarks>
/// 用 response／damping 而不是剛度與阻尼係數，是因為這兩個數字讀得出手感：
/// response 大約就是走完的時間，damping 小於 1 才會回彈，越接近 1 回彈越少。
/// </remarks>
internal readonly struct SpringParameters
{
    public SpringParameters(double response, double damping)
    {
        if (response <= 0) throw new ArgumentOutOfRangeException(nameof(response));
        if (damping <= 0) throw new ArgumentOutOfRangeException(nameof(damping));
        Response = response; Damping = damping;
    }

    /// <summary>通知島的預設：約 0.38 秒、阻尼 0.82，回彈約 1%，400 ms 內收斂。</summary>
    public static SpringParameters Default { get; } = new(0.38, 0.82);

    public double Response { get; }
    public double Damping { get; }

    internal double Stiffness => Math.Pow(2 * Math.PI / Response, 2);
    internal double Friction => 4 * Math.PI * Damping / Response;
}

/// <summary>彈簧某一刻的狀態：位置與速度。</summary>
internal readonly struct SpringState
{
    public SpringState(double value, double velocity) { Value = value; Velocity = velocity; }

    public double Value { get; }
    public double Velocity { get; }

    /// <summary>離目標與速度都小到畫面上看不出差別。</summary>
    public bool IsSettled(double target, double tolerance = SpringMotion.Tolerance) =>
        Math.Abs(Value - target) < tolerance && Math.Abs(Velocity) < tolerance * 10;

    /// <summary>
    /// 往 <paramref name="target"/> 積分 <paramref name="seconds"/> 秒。
    /// </summary>
    /// <remarks>
    /// 半隱式 Euler，每一小步不超過 1/240 秒：一格 16 ms 直接積分在剛度這麼高的彈簧上
    /// 會越積越發散，而掉格時一次給 50 ms 更是如此。純函式，不看時鐘，測試直接餵時間。
    /// </remarks>
    public SpringState Step(double target, SpringParameters parameters, double seconds)
    {
        if (seconds <= 0) return this;
        var value = Value; var velocity = Velocity;
        var steps = (int)Math.Ceiling(seconds / MaxStep);
        var dt = seconds / steps;
        for (var i = 0; i < steps; i++)
        {
            var acceleration = -parameters.Stiffness * (value - target) - parameters.Friction * velocity;
            velocity += acceleration * dt;
            value += velocity * dt;
        }

        return new SpringState(value, velocity);
    }

    private const double MaxStep = 1.0 / 240;
}

/// <summary>
/// 逐幀積分的單一數值彈簧；通知島的寬、高與圓角各一份。
/// </summary>
/// <remarks>
/// 不用 <c>EasingFunction</c>：補間動畫的起點、終點與時長在開始時就定死，中途改目標只能
/// 從目前值重新開一段，速度歸零，看起來是頓一下再掉頭。動態島的手感來自「可中斷、可反向」，
/// 那需要保留速度，所以狀態是 (value, velocity)，每一幀往最新的目標積分。
///
/// 由 <see cref="CompositionTarget.Rendering"/> 驅動，只在還沒靜止時訂閱，停下來當場取消：
/// 那是全域的每幀事件，留著訂閱等於閒置時也每一幀喚醒一次。
/// 動畫關著（減少動態效果、高對比）時 <see cref="AnimateTo"/> 直接跳到目標值。
/// </remarks>
internal sealed class SpringMotion
{
    /// <summary>位置差多少以內算是到了；DIP 單位，比半個裝置像素還小。</summary>
    public const double Tolerance = 0.01;

    /// <summary>掉格時一次最多積分這麼久，免得卡頓後一步跳到終點。</summary>
    private static readonly TimeSpan MaxFrame = TimeSpan.FromMilliseconds(50);

    private readonly Action<double> _apply;
    private SpringState _state;
    private TimeSpan? _lastFrame;
    private bool _subscribed;

    public SpringMotion(double initial, Action<double> apply, SpringParameters? parameters = null)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _state = new SpringState(initial, 0);
        Target = initial;
        Parameters = parameters ?? SpringParameters.Default;
    }

    public SpringParameters Parameters { get; }
    public double Target { get; private set; }
    public double Value => _state.Value;
    public double Velocity => _state.Velocity;

    /// <summary>還在動，也就還訂閱著每幀事件。</summary>
    public bool IsActive => _subscribed;

    /// <summary>到了目標而停下來；通知島用它在收縮結束後才淡掉或移除內容。</summary>
    public event EventHandler? Settled;

    /// <summary>換一個目標；還在動的話保留目前速度，從這一刻接著走。</summary>
    public void AnimateTo(double target, bool motion)
    {
        Target = target;
        if (!motion) { Jump(target); return; }
        if (_state.IsSettled(target)) { Jump(target); return; }
        if (_subscribed) return;
        _subscribed = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>直接放到某個值、速度歸零並停止。</summary>
    public void Jump(double value)
    {
        var wasActive = _subscribed;
        Unsubscribe();
        Target = value;
        _state = new SpringState(value, 0);
        _apply(value);
        if (wasActive) Settled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>停在目前位置；卸載或表面離開畫面時用，不觸發 <see cref="Settled"/>。</summary>
    public void Stop()
    {
        Unsubscribe();
        _state = new SpringState(_state.Value, 0);
    }

    /// <summary>往前走一段時間；每幀事件與測試都走這一條。回傳是否還在動。</summary>
    internal bool Advance(TimeSpan elapsed)
    {
        if (elapsed > MaxFrame) elapsed = MaxFrame;
        _state = _state.Step(Target, Parameters, elapsed.TotalSeconds);
        if (_state.IsSettled(Target))
        {
            Unsubscribe();
            _state = new SpringState(Target, 0);
            _apply(Target);
            Settled?.Invoke(this, EventArgs.Empty);
            return false;
        }

        _apply(_state.Value);
        return true;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        // RenderingTime 是這一幀的合成時間，同一幀可能觸發多次；時間沒前進就不積分。
        var now = args is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        var previous = _lastFrame;
        _lastFrame = now;
        if (previous is not { } last) { Advance(TimeSpan.FromSeconds(1.0 / 60)); return; }
        if (now > last) Advance(now - last);
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
    }
}
