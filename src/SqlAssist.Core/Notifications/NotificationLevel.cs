namespace SqlAssist.Core.Notifications;

/// <summary>詳細度門檻，由低到高。</summary>
public enum NotificationLevel
{
    /// <summary>只進診斷統計；詳細度選到「全部」才上畫面。</summary>
    Trace,
    /// <summary>只在詳細度到「詳細」時上畫面。</summary>
    Debug,
    /// <summary>一般工作；看該種類的開關。</summary>
    Info,
    /// <summary>值得知道的事，跨過種類開關。</summary>
    Notice,
}
