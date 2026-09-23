using System;

namespace SqlAssist.Core.Notifications;

/// <summary>提醒上的一顆按鈕：穩定識別字、標籤、分量與參數。</summary>
/// <remarks>
/// 不存委派：快照要維持不可變、可以比對，而使用者按下去的決定（例如略過哪一版）要能
/// 落地保存——委派做不到後者，也讓測試只能靠呼叫副作用判斷按了哪一顆。
/// 按下之後由呼叫端依 <see cref="Id"/> 處理，<see cref="Argument"/> 帶版本號、網址這類參數。
/// </remarks>
public sealed class NotificationAction
{
    public NotificationAction(string id, string label, NotificationActionRole role, string argument = "")
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("按鈕需要穩定的識別字。", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("按鈕需要標籤。", nameof(label));
        Id = id; Label = label; Role = role; Argument = argument ?? "";
    }

    /// <summary>穩定的點分字串，例如 <c>update.download</c>；診斷紀錄只寫它，不寫標籤。</summary>
    public string Id { get; }

    public string Label { get; }
    public NotificationActionRole Role { get; }

    /// <summary>處理這顆按鈕需要的參數（版本號、網址）；沒有時是空字串。</summary>
    public string Argument { get; }
}
