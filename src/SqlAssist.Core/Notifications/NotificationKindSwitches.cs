using System;
using System.Linq;

namespace SqlAssist.Core.Notifications;

/// <summary>每個 <see cref="NotificationKind"/> 的顯示開關，壓成一個位元遮罩。</summary>
/// <remarks>
/// 不用字典也不用陣列，是因為 <c>SqlAssistSettings</c> 的守門測試整份快照逐屬性比對，
/// 而字典與陣列比的是參考：兩份內容相同的預設快照會被判定成不相等，
/// 「讀不到任何設定時回退為預設值」就永遠失敗。遮罩是值，相等比較自然成立。
///
/// 新增一個種類只動 <see cref="NotificationKindToggle.All"/>，這裡不必跟著改。
/// </remarks>
public readonly struct NotificationKindSwitches : IEquatable<NotificationKindSwitches>
{
    private readonly int _mask;

    private NotificationKindSwitches(int mask) => _mask = mask;

    /// <summary><see cref="NotificationKindToggle.EnabledByDefault"/> 組成的那一份。</summary>
    public static NotificationKindSwitches Defaults { get; } = Build();

    public bool this[NotificationKind kind] => (_mask & Bit(kind)) != 0;

    public NotificationKindSwitches With(NotificationKind kind, bool enabled) =>
        new(enabled ? _mask | Bit(kind) : _mask & ~Bit(kind));

    public bool Equals(NotificationKindSwitches other) => _mask == other._mask;

    public override bool Equals(object? obj) => obj is NotificationKindSwitches other && Equals(other);

    public override int GetHashCode() => _mask;

    public static bool operator ==(NotificationKindSwitches left, NotificationKindSwitches right) => left.Equals(right);

    public static bool operator !=(NotificationKindSwitches left, NotificationKindSwitches right) => !left.Equals(right);

    /// <summary>快照比對失敗時要看得出是哪幾類開著，所以列出名稱而不是印遮罩。</summary>
    public override string ToString()
    {
        var mask = _mask;
        var enabled = NotificationKindToggle.All
            .Where(toggle => (mask & Bit(toggle.Kind)) != 0)
            .Select(toggle => toggle.Kind.ToString());

        return string.Join("+", enabled) is { Length: > 0 } text ? text : "(全部關閉)";
    }

    private static int Bit(NotificationKind kind) => 1 << (int)kind;

    private static NotificationKindSwitches Build()
    {
        var mask = 0;

        foreach (var toggle in NotificationKindToggle.All)
        {
            if (toggle.EnabledByDefault)
            {
                mask |= Bit(toggle.Kind);
            }
        }

        return new NotificationKindSwitches(mask);
    }
}
