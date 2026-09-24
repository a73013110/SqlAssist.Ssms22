using System;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 通知島錨在哪一個視窗：使用者正在操作的框架，其次是目前的錨點，最後是主視窗。
/// </summary>
/// <remarks>
/// 泛型只為了不碰真的視窗就能測；呼叫端傳 <see cref="System.Windows.Window"/>。
///
/// 沿用目前的錨點是為了不跳：焦點跑到別的程式、對話框或 WinForms 視窗時沒有作用中的框架，
/// 這時改回主視窗的話，島嶼會在兩個視窗之間來回重播。
/// 目前的錨點看不到（最小化、隱藏、關掉）就改用主視窗，而不是等它回來——
/// 等的版本會讓拆出去的框架一最小化，之後的通知就全都看不到。
/// </remarks>
internal static class NotificationAnchor
{
    /// <returns>三者都看不到時為 null：整個 SSMS 最小化，等還原再長出來。</returns>
    public static T? Choose<T>(T? active, T? current, T? main, Func<T, bool> showing) where T : class
    {
        if (showing is null) throw new ArgumentNullException(nameof(showing));
        if (active is not null && showing(active)) return active;
        if (current is not null && showing(current)) return current;
        return main is not null && showing(main) ? main : null;
    }
}
