using System.Runtime.Serialization;

namespace SqlAssist.Core;

[DataContract]
public sealed class SqlAssistSettings
{
    [DataMember(Name = "enabled", Order = 1)]
    public bool Enabled { get; set; } = true;

    [DataMember(Name = "features", Order = 2)]
    public SqlAssistFeatureSettings Features { get; set; } = new();

    [DataMember(Name = "suggestions", Order = 3)]
    public SqlAssistSuggestionSettings Suggestions { get; set; } = new();

    [DataMember(Name = "diagnosticsEnabled", Order = 4)]
    public bool DiagnosticsEnabled { get; set; }

    /// <summary>
    /// 把非同步建議管線的每一步寫進診斷紀錄。
    /// </summary>
    /// <remarks>
    /// 原本用來量測平台原生 IntelliSense 是否可用，量測已完成（可用），
    /// 現在保留為疑難排解用的追蹤開關。
    /// </remarks>
    [DataMember(Name = "asyncCompletionProbe", Order = 5)]
    public bool AsyncCompletionProbe { get; set; }

    public SqlAssistSettings Clone()
    {
        return new SqlAssistSettings
        {
            Enabled = Enabled,
            Features = Features.Clone(),
            Suggestions = Suggestions.Clone(),
            DiagnosticsEnabled = DiagnosticsEnabled,
            AsyncCompletionProbe = AsyncCompletionProbe
        };
    }
}
