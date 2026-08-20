namespace SqlAssist.Core;

public sealed class SqlCompletionContext
{
    public SqlCompletionContext(
        bool isValid,
        int tokenStart,
        string prefix,
        CompletionTarget target,
        string? schemaQualifier = null)
    {
        IsValid = isValid;
        TokenStart = tokenStart;
        Prefix = prefix;
        Target = target;
        SchemaQualifier = schemaQualifier;
    }

    public bool IsValid { get; }

    public int TokenStart { get; }

    public string Prefix { get; }

    public CompletionTarget Target { get; }

    public string? SchemaQualifier { get; }
}
