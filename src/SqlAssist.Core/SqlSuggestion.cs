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
        string? schemaName = null,
        object? tag = null,
        SqlKeywordPosition positions = SqlKeywordPosition.Any)
    {
        DisplayText = displayText;
        InsertionText = insertionText;
        Description = description;
        Preview = preview;
        Kind = kind;
        TriggerFollowUp = triggerFollowUp;
        SchemaName = schemaName;
        Tag = tag;
        Positions = positions;
    }

    public string DisplayText { get; }

    public string InsertionText { get; }

    public string Description { get; }

    public string Preview { get; }

    public SuggestionKind Kind { get; }

    public bool TriggerFollowUp { get; }

    public string? SchemaName { get; }

    /// <summary>
    /// 建立這筆建議的來源資料。資料庫物件會放入中繼資料層的物件描述，
    /// 讓呼叫端可以在使用者選取時才去載入欄位與定義，而不必在建立建議時就全部帶齊。
    /// </summary>
    public object? Tag { get; }

    /// <summary>
    /// 這筆建議可以出現的位置。
    /// </summary>
    /// <remarks>
    /// 只有關鍵字會收斂到特定位置；資料庫物件與 Snippet 一律是
    /// <see cref="SqlKeywordPosition.Any"/>，它們的過濾走
    /// <see cref="CompletionTarget"/> 那條路。
    /// </remarks>
    public SqlKeywordPosition Positions { get; }
}
