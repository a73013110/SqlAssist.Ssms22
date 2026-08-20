namespace SqlAssist.Core;

public sealed class SqlSuggestion
{
    public SqlSuggestion(
        string displayText,
        string insertionText,
        string description,
        string preview,
        SuggestionKind kind,
        bool triggerFollowUp = false,
        string? schemaName = null)
    {
        DisplayText = displayText;
        InsertionText = insertionText;
        Description = description;
        Preview = preview;
        Kind = kind;
        TriggerFollowUp = triggerFollowUp;
        SchemaName = schemaName;
    }

    public string DisplayText { get; }

    public string InsertionText { get; }

    public string Description { get; }

    public string Preview { get; }

    public SuggestionKind Kind { get; }

    public bool TriggerFollowUp { get; }

    public string? SchemaName { get; }
}
