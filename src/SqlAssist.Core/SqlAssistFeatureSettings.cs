using System.Runtime.Serialization;

namespace SqlAssist.Core;

[DataContract]
public sealed class SqlAssistFeatureSettings
{
    [DataMember(Name = "tabExpansion", Order = 1)]
    public bool TabExpansion { get; set; } = true;

    [DataMember(Name = "keywordUppercase", Order = 2)]
    public bool KeywordUppercase { get; set; } = true;

    [DataMember(Name = "objectPicker", Order = 3)]
    public bool ObjectPicker { get; set; } = true;

    [DataMember(Name = "resultGridCommands", Order = 4)]
    public bool ResultGridCommands { get; set; } = true;

    public bool Get(SqlAssistFeature feature)
    {
        return feature switch
        {
            SqlAssistFeature.TabExpansion => TabExpansion,
            SqlAssistFeature.KeywordUppercase => KeywordUppercase,
            SqlAssistFeature.ObjectPicker => ObjectPicker,
            SqlAssistFeature.ResultGridCommands => ResultGridCommands,
            _ => false
        };
    }

    public bool Toggle(SqlAssistFeature feature)
    {
        var value = !Get(feature);

        switch (feature)
        {
            case SqlAssistFeature.TabExpansion:
                TabExpansion = value;
                break;
            case SqlAssistFeature.KeywordUppercase:
                KeywordUppercase = value;
                break;
            case SqlAssistFeature.ObjectPicker:
                ObjectPicker = value;
                break;
            case SqlAssistFeature.ResultGridCommands:
                ResultGridCommands = value;
                break;
        }

        return value;
    }

    public SqlAssistFeatureSettings Clone()
    {
        return new SqlAssistFeatureSettings
        {
            TabExpansion = TabExpansion,
            KeywordUppercase = KeywordUppercase,
            ObjectPicker = ObjectPicker,
            ResultGridCommands = ResultGridCommands
        };
    }
}

