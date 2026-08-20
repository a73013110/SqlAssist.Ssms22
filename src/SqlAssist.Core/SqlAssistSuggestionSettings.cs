using System.Runtime.Serialization;

namespace SqlAssist.Core;

[DataContract]
public sealed class SqlAssistSuggestionSettings
{
    [DataMember(Name = "enabled", Order = 1)]
    public bool Enabled { get; set; } = true;

    [DataMember(Name = "triggerAfterCharacters", Order = 2)]
    public int TriggerAfterCharacters { get; set; } = 1;

    [DataMember(Name = "maximumItems", Order = 3)]
    public int MaximumItems { get; set; } = 100;

    [DataMember(Name = "showPreview", Order = 4)]
    public bool ShowPreview { get; set; } = true;

    [DataMember(Name = "delayMilliseconds", Order = 5)]
    public int DelayMilliseconds { get; set; } = 70;

    [DataMember(Name = "qualifyObjectNames", Order = 6)]
    public bool QualifyObjectNames { get; set; }

    [DataMember(Name = "useSquareBrackets", Order = 7)]
    public bool UseSquareBrackets { get; set; }

    public SqlAssistSuggestionSettings Clone()
    {
        return new SqlAssistSuggestionSettings
        {
            Enabled = Enabled,
            TriggerAfterCharacters = TriggerAfterCharacters,
            MaximumItems = MaximumItems,
            ShowPreview = ShowPreview,
            DelayMilliseconds = DelayMilliseconds,
            QualifyObjectNames = QualifyObjectNames,
            UseSquareBrackets = UseSquareBrackets
        };
    }
}
