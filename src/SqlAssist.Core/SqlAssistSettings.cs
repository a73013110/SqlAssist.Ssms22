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
    /// 讓探測用的非同步建議來源實際提供項目。預設關閉，只用來量測
    /// 平台原生 IntelliSense 在 SSMS 的 SQL 編輯器裡是否可用。
    /// </summary>
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
