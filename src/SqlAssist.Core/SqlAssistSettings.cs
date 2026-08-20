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

    public SqlAssistSettings Clone()
    {
        return new SqlAssistSettings
        {
            Enabled = Enabled,
            Features = Features.Clone(),
            Suggestions = Suggestions.Clone(),
            DiagnosticsEnabled = DiagnosticsEnabled
        };
    }
}
