using System;
using System.Collections.Generic;

namespace SqlAssist.Ssms22.Notifications;

/// <summary>
/// 提醒按鈕的派送：識別字對到處理常式，按下時先收掉那一則再派送。
/// </summary>
/// <remarks>
/// 提醒只存穩定識別字與參數，不存委派（理由見 <c>Core/Notifications/NotificationAction</c>）；
/// 誰來處理在套件初始化時一次登記。先收掉再派送：處理常式開的是視窗或瀏覽器，
/// 反過來的話那則提醒會在新視窗後面多留一輪，看起來像按了沒反應。
///
/// 只在 UI 執行緒上登記與呼叫。
/// </remarks>
internal static class NotificationActionRouter
{
    private static readonly Dictionary<string, Action<string>> Handlers = new(StringComparer.Ordinal);

    /// <summary>登記一個識別字的處理常式；參數是按鈕帶的 <c>Argument</c>。</summary>
    public static void Register(string actionId, Action<string> handler)
    {
        if (string.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("需要按鈕識別字。", nameof(actionId));
        Handlers[actionId] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>使用者按了提醒上的按鈕，或叉號（<paramref name="actionId"/> 為 null，只收掉）。</summary>
    public static void Invoke(long promptId, string? actionId)
    {
        if (!NotificationPresenter.Default.TryResolve(promptId, actionId, out var action) || action is null) return;
        if (!Handlers.TryGetValue(action.Id, out var handler))
        {
            SqlAssistDiagnostics.WriteAlways($"提醒按鈕沒有處理常式：{action.Id}");
            return;
        }

        SqlAssistPlatformGuard.Run("處理提醒按鈕 " + action.Id, () => handler(action.Argument));
    }
}
